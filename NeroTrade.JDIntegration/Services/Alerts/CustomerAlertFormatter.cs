using System.Text.Json;
using System.Text.RegularExpressions;
using NeroTrade.JDIntegration.Services.Logging;

namespace NeroTrade.JDIntegration.Services.Alerts;

public sealed record CustomerAlert(string Subject, string Body, string DedupKey);

/// <summary>
/// Turns a "not sent to JD" log row into a short plain-Danish mail: what was not sent, why, and what the customer
/// can do about it.
///
/// A row is mailed only when its call site opted in by setting <see cref="IntegrationLogEntry.AlertSubject"/>.
/// That keeps the decision next to the code that knows the row means "the customer must act" — transport
/// hiccups, retries, PDF side-issues and duplicate-catalog cleanups (ours to fix) carry no subject and stay in
/// Supabase/Slack. Level is not part of the rule: an item missing in Uniconta is logged as a warning (the sync
/// carries on) but is still something only the customer can fix.
/// </summary>
public static class CustomerAlertFormatter
{
    private const string NoCatalogMarker = "No matching JD catalog item for SKU(s):";

    private static readonly Regex ProductNotFound = new(@"couldn't find any product with the SKU:\s*(?<sku>[^\s""]+)", RegexOptions.IgnoreCase);
    private static readonly Regex LowStock = new(@"couldn't fulfill SKU:\s*(?<sku>[^\s""]+)\s+due to low stock\.?\s*Current stock:\s*(?<have>-?\d+),\s*requested stock:\s*(?<want>-?\d+)", RegexOptions.IgnoreCase);

    /// <param name="autoCreateMissingItems">
    /// Mirrors <c>PurchaseOrderSync:AutoCreateMissingItems</c>. When on, a purchase order that still lacks a
    /// catalog item means the automatic creation failed, so "set Overfør vare" would be the wrong advice.
    /// </param>
    public static CustomerAlert? TryFormat(IntegrationLogEntry entry, bool autoCreateMissingItems = false)
    {
        if (entry.AlertSubject is not { } subject) return null;
        if (string.IsNullOrWhiteSpace(entry.ExternalId)) return null;
        var id = entry.ExternalId.Trim();
        var reason = (entry.AlertReason ?? entry.Message).Trim();
        var dedupKey = $"{subject}:{entry.ErrorCode}:{id}";

        if (subject == CustomerAlertSubject.CatalogItem)
            return FormatItem(entry, id, reason, dedupKey);

        var (noun, subjectLine) = subject switch
        {
            CustomerAlertSubject.SalesOrder => ("ordren", $"Ordre {id} blev ikke sendt til JD"),
            CustomerAlertSubject.PurchaseOrder => ("indkøbet", $"Indkøb {id} blev ikke sendt til JD"),
            _ => ("det bogførte indkøb", $"Bogført indkøb {id} blev ikke sendt til JD"),
        };
        var label = subject switch
        {
            CustomerAlertSubject.SalesOrder => $"Ordre {id}",
            CustomerAlertSubject.PurchaseOrder => $"Indkøb {id}",
            _ => $"Det bogførte indkøb {id}",
        };
        var resend = subject == CustomerAlertSubject.SalesOrder
            ? "Send derefter ordren igen med \"Overfør til JD\"."
            : $"Send derefter {noun} igen med \"Overfør til JD\".";

        var (why, fix) = Explain(reason, subject, autoCreateMissingItems);
        return new CustomerAlert(subjectLine, $"{label} er ikke sendt til JD, fordi {why}\n\n{fix} {resend}", dedupKey);
    }

    private static CustomerAlert? FormatItem(IntegrationLogEntry entry, string id, string reason, string dedupKey)
    {
        if (entry.ErrorCode == "UNICONTA_LOOKUP_MISS")
            return new CustomerAlert(
                $"Varen {id} findes ikke i Uniconta",
                $"Varen {id} står på et indkøb, men findes ikke i Uniconta, så den kan ikke oprettes hos JD.\n\n" +
                "Tjek varenummeret på indkøbslinjen, eller opret varen i Uniconta, og send indkøbet igen.",
                dedupKey);

        var why = TranslateFieldErrors(reason) is { } fields
            ? $"JD mangler eller afviser disse oplysninger: {fields}"
            : $"JD svarede: {reason}";
        return new CustomerAlert(
            $"Varen {id} kunne ikke oprettes hos JD",
            $"Varen {id} er ikke oprettet eller opdateret hos JD. {why}\n\n" +
            "Ret varen i Uniconta. Den bliver sendt til JD igen af sig selv inden for få minutter. " +
            "Står den på et indkøb, så send indkøbet igen bagefter.",
            dedupKey);
    }

    /// <summary>The "because …" clause and the fix, for the reject reasons JD has actually sent us.</summary>
    private static (string Why, string Fix) Explain(string reason, CustomerAlertSubject subject, bool autoCreate)
    {
        if (reason.Contains(NoCatalogMarker, StringComparison.Ordinal))
        {
            var skus = ExtractSkus(reason);
            var plural = skus.Contains(',');
            var thing = plural ? $"varerne {skus}" : $"varen {skus}";
            return autoCreate
                ? ($"{thing} ikke findes hos JD og ikke kunne oprettes automatisk.",
                   $"Tjek at {(plural ? "varerne" : "varen")} findes i Uniconta med navn, vægt og oprindelsesland. Du får en separat mail, hvis JD har afvist {(plural ? "dem" : "den")}.")
                : ($"{thing} ikke findes hos JD.",
                   $"Sæt \"Overfør vare\" på {(plural ? "varerne" : "varen")} i Uniconta, og vent et par minutter, så {(plural ? "de" : "den")} bliver oprettet hos JD.");
        }

        if (ProductNotFound.Match(reason) is { Success: true } nf)
        {
            var sku = nf.Groups["sku"].Value;
            return ($"varen {sku} ikke findes hos JD.",
                    $"Tjek varenummeret på linjen. Skal varen ligge hos JD, så sæt \"Overfør vare\" på den i Uniconta og vent et par minutter.");
        }

        if (LowStock.Match(reason) is { Success: true } ls)
            return ($"JD kun har {ls.Groups["have"].Value} stk. af varen {ls.Groups["sku"].Value} på lager, og der er bestilt {ls.Groups["want"].Value}.",
                    "Ret antallet på ordren, eller vent til varen er på lager hos JD.");

        if (reason.Contains("doesn't have any inventory items", StringComparison.OrdinalIgnoreCase))
            return ("der ikke er nogen lagervarer på den.",
                    "Tjek linjerne. Kun varer der ligger på lager hos JD bliver sendt med.");

        if (TranslateFieldErrors(reason) is { } fields)
            return ($"JD mangler eller afviser disse oplysninger: {fields}.",
                    $"Ret det i Uniconta.");

        return ($"JD afviste {(subject == CustomerAlertSubject.SalesOrder ? "den" : "det")}. JD svarede: {reason}",
                "Ret oplysningerne i Uniconta. Skriv til Mark, hvis du er i tvivl om, hvad der skal rettes.");
    }

    /// <summary>
    /// JD's field validation comes back as <c>{"Field.Path":["message", …]}</c>. Known fields are put in plain
    /// Danish; unknown ones keep JD's text so nothing is lost. Null when the reason is not that shape.
    /// </summary>
    internal static string? TranslateFieldErrors(string reason)
    {
        var start = reason.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(reason[start..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) return null; // e.g. {"error":"…"}: not field errors
                var text = string.Join(" ", prop.Value.EnumerateArray().Select(v => v.ToString()));
                parts.Add(TranslateField(prop.Name, text));
            }
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TranslateField(string field, string text)
    {
        var key = field.ToLowerInvariant();
        if (key == "date" && text.Contains("greater than or equal", StringComparison.OrdinalIgnoreCase))
            return "leveringsdatoen ligger før i dag (ret den til i dag eller senere)";
        if (key.Contains("producedincountrycode"))
            return "oprindelseslandet skal være en landekode på 2 bogstaver, fx DK eller CN";
        if (key.Contains("contactperson.name"))
            return "navn på kontaktperson/modtager mangler på leveringsadressen";
        if (key.Contains("contactperson.phone"))
            return "telefonnummer på modtageren mangler eller er ugyldigt";
        if (key.Contains("contactperson.email"))
            return "e-mail på modtageren mangler eller er ugyldig";
        if (key.Contains("zip") || key.Contains("postal"))
            return "postnummeret på leveringsadressen mangler eller er ugyldigt";
        if (key.Contains("weight"))
            return "vægten på varen mangler eller er ugyldig";
        return $"{field}: {text}";
    }

    private static string ExtractSkus(string message)
    {
        var start = message.IndexOf(NoCatalogMarker, StringComparison.Ordinal) + NoCatalogMarker.Length;
        var rest = message[start..].Trim();
        var end = rest.IndexOf(". ", StringComparison.Ordinal);
        return (end >= 0 ? rest[..end] : rest.TrimEnd('.')).Trim();
    }
}

using NeroTrade.JDIntegration.Services.Logging;

namespace NeroTrade.JDIntegration.Services.Alerts;

public sealed record CustomerAlert(string Subject, string Body, string DedupKey);

/// <summary>
/// Decides which log rows are worth a mail to the customer and words them in plain Danish: what was not sent,
/// why, and what the customer can do about it. Deliberately narrow — only problems the customer can act on.
/// Transport hiccups, retries and warnings stay in Supabase/Slack.
/// </summary>
public static class CustomerAlertFormatter
{
    private const string NoCatalogMarker = "No matching JD catalog item for SKU(s):";

    public static CustomerAlert? TryFormat(IntegrationLogEntry entry)
    {
        if (!string.Equals(entry.Level, "error", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(entry.ExternalId)) return null;
        var id = entry.ExternalId.Trim();

        switch (entry.ErrorCode)
        {
            case "JD_VALIDATION_REJECTED" when entry.Message.Contains(NoCatalogMarker, StringComparison.Ordinal):
            {
                var skus = ExtractSkus(entry.Message);
                return new CustomerAlert(
                    $"Indkøb {id} blev ikke sendt til JD",
                    $"Indkøb {id} er ikke sendt til JD, fordi {(skus.Contains(',') ? "varerne" : "varen")} {skus} ikke findes i JD's katalog.\n\n" +
                    "Sådan løser du det: Sæt \"Overfør vare\" på varen i Uniconta, og vent et par minutter, så varen bliver oprettet hos JD. " +
                    $"Send derefter indkøb {id} igen.",
                    $"{entry.ErrorCode}:{id}");
            }

            case "JD_VALIDATION_REJECTED":
                return new CustomerAlert(
                    $"JD har afvist {id}",
                    $"JD har afvist {id} og har ikke modtaget det.\n\nÅrsag fra JD: {entry.Message}\n\n" +
                    "Ret oplysningerne i Uniconta og send igen. Skriv til Mark, hvis du er i tvivl om, hvad der skal rettes.",
                    $"{entry.ErrorCode}:{id}");

            case "UNICONTA_LOOKUP_MISS":
                return new CustomerAlert(
                    $"Varen {id} findes ikke i Uniconta",
                    $"Varen {id} står på et indkøb, men findes ikke i Uniconta, så den kan ikke oprettes hos JD.\n\n" +
                    "Tjek varenummeret på indkøbslinjen, eller opret varen i Uniconta.",
                    $"{entry.ErrorCode}:{id}");

            default:
                return null;
        }
    }

    private static string ExtractSkus(string message)
    {
        var start = message.IndexOf(NoCatalogMarker, StringComparison.Ordinal) + NoCatalogMarker.Length;
        var rest = message[start..].Trim();
        var end = rest.IndexOf(". ", StringComparison.Ordinal);
        return (end >= 0 ? rest[..end] : rest.TrimEnd('.')).Trim();
    }
}

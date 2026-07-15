using System.Text.RegularExpressions;
using NeroTrade.JDIntegration.Models.ExternalIntegration;

namespace NeroTrade.JDIntegration.Services.ExternalIntegration;

public static class JdOrderHelper
{
    // Regex to extract Order Number from "SO {OrderNumber} - {Remark}"
    // Matches "SO 12345", "so 12345", etc. Used for the legacy text/deliveryNoteText fields where the
    // key LED the field, so the leftmost match wins.
    private static readonly Regex OrderNumberRegex = new(@"SO\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex to extract the "SO {n}" key that SalesOrderMapper APPENDS to trackingNote as
    // "{Sporingsnote} / SO {n}" (or bare "SO {n}"). It is anchored to the END and requires the key to be
    // either at the string start or preceded by our exact " / " separator, so a stray "SO …" embedded in
    // a free-text Sporingsnote (e.g. "ref til SO 9999") does NOT false-match — only the key we appended.
    private static readonly Regex TrackingNoteOrderNumberRegex = new(@"(?:^|\s/\s)SO\s+(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex to extract Purchase Order Number from "PO {OrderNumber}"
    private static readonly Regex PurchaseOrderNumberRegex = new(@"PO\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the order number as a string from shopOrderId, trackingNote, the text field, or (fallback)
    /// deliveryNoteText. Priority: shopOrderId → trackingNote → text → deliveryNoteText. Current outgoing
    /// orders append the "SO {n}" machine key to <c>trackingNote</c> ("{Sporingsnote} / SO {n}"); the text
    /// fallback covers legacy orders that led the key on <c>text</c> ("Intern Note"), and deliveryNoteText
    /// covers even older orders that carried it on the delivery-note text. The trackingNote match is
    /// end-anchored so a stray "SO …" inside a free Sporingsnote is ignored.
    /// </summary>
    public static string? GetOrderNumberString(string? shopOrderId, string? trackingNote, string? text, string? deliveryNoteText = null)
    {
        if (!string.IsNullOrWhiteSpace(shopOrderId))
        {
            return shopOrderId!.Trim();
        }

        return MatchTrackingNoteOrderNumber(trackingNote)
            ?? MatchOrderNumber(text)
            ?? MatchOrderNumber(deliveryNoteText);
    }

    private static string? MatchOrderNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = OrderNumberRegex.Match(value);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? MatchTrackingNoteOrderNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = TrackingNoteOrderNumberRegex.Match(value);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Extracts the order number as an int from shopOrderId, trackingNote, the text field, or (fallback)
    /// deliveryNoteText. Returns 0 if parsing fails. See <see cref="GetOrderNumberString"/> for the lookup priority.
    /// </summary>
    public static int GetOrderNumber(string? shopOrderId, string? trackingNote, string? text, string? deliveryNoteText = null)
    {
        var str = GetOrderNumberString(shopOrderId, trackingNote, text, deliveryNoteText);
        if (int.TryParse(str, out var val))
        {
            return val;
        }
        return 0;
    }

    /// <summary>
    /// Extracts the purchase order number as a string from the text field.
    /// </summary>
    public static string? GetPurchaseOrderNumberString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = PurchaseOrderNumberRegex.Match(text);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Extracts the purchase order number as an int from the text field.
    /// Returns 0 if parsing fails.
    /// </summary>
    public static int GetPurchaseOrderNumber(string? text)
    {
        var str = GetPurchaseOrderNumberString(text);
        if (int.TryParse(str, out var val))
        {
            return val;
        }
        return 0;
    }

    /// <summary>
    /// Calculates total received quantities per SKU from registered items.
    /// Handles the tree structure by only summing leaf nodes.
    /// </summary>
    public static Dictionary<string, int> CalculateReceivedQuantitiesBySku(List<JdRegisteredItem> registeredItems)
    {
        if (registeredItems == null || registeredItems.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        // 1. Collect all IDs that are used as parentId by other items
        var parentIds = registeredItems
            .Where(i => i.parentId.HasValue)
            .Select(i => i.parentId!.Value)
            .ToHashSet();

        // 2. Filter to items whose ID is not in that set (leaf nodes)
        var leafItems = registeredItems
            .Where(i => i.id.HasValue && !parentIds.Contains(i.id.Value));

        // 3. Group by catalog.sku and sum quantities
        return leafItems
            .Where(i => !string.IsNullOrWhiteSpace(i.catalog?.sku))
            .GroupBy(i => i.catalog!.sku!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.quantity), StringComparer.OrdinalIgnoreCase);
    }
}


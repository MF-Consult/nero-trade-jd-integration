using System.Text.RegularExpressions;
using NeroTrade.JDIntegration.Models.ExternalIntegration;

namespace NeroTrade.JDIntegration.Services.ExternalIntegration;

public static class JdOrderHelper
{
    // Regex to extract Order Number from "SO {OrderNumber} - {Remark}"
    // Matches "SO 12345", "so 12345", etc.
    private static readonly Regex OrderNumberRegex = new(@"SO\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex to extract Purchase Order Number from "PO {OrderNumber}"
    private static readonly Regex PurchaseOrderNumberRegex = new(@"PO\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the order number as a string from shopOrderId, the text field, or (fallback) deliveryNoteText.
    /// Prioritizes shopOrderId. The deliveryNoteText fallback exists because the "SO {n} - {remark}" reference
    /// is now written to deliveryNoteText rather than text on outgoing orders.
    /// </summary>
    public static string? GetOrderNumberString(string? shopOrderId, string? text, string? deliveryNoteText = null)
    {
        if (!string.IsNullOrWhiteSpace(shopOrderId))
        {
            return shopOrderId!.Trim();
        }

        return MatchOrderNumber(text) ?? MatchOrderNumber(deliveryNoteText);
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

    /// <summary>
    /// Extracts the order number as an int from shopOrderId, the text field, or (fallback) deliveryNoteText.
    /// Returns 0 if parsing fails.
    /// </summary>
    public static int GetOrderNumber(string? shopOrderId, string? text, string? deliveryNoteText = null)
    {
        var str = GetOrderNumberString(shopOrderId, text, deliveryNoteText);
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


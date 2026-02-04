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
    /// Extracts the order number as a string from either shopOrderId or the text field.
    /// Prioritizes shopOrderId.
    /// </summary>
    public static string? GetOrderNumberString(string? shopOrderId, string? text)
    {
        if (!string.IsNullOrWhiteSpace(shopOrderId))
        {
            return shopOrderId!.Trim();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = OrderNumberRegex.Match(text);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Extracts the order number as an int from either shopOrderId or the text field.
    /// Returns 0 if parsing fails.
    /// </summary>
    public static int GetOrderNumber(string? shopOrderId, string? text)
    {
        var str = GetOrderNumberString(shopOrderId, text);
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


using System.Text.RegularExpressions;

namespace NeroTrade.JDIntegration.Services.ExternalIntegration;

public static class JdOrderHelper
{
    // Regex to extract Order Number from "SO {OrderNumber} - {Remark}"
    // Matches "SO 12345", "so 12345", etc.
    private static readonly Regex OrderNumberRegex = new(@"SO\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
}


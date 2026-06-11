namespace NeroTrade.UnicontaPlugin;

/// <summary>
/// Normalizes raw GetUserField results for enum (value-list) user fields.
/// Uniconta returns the string value in practice, but the storage format is not
/// guaranteed across versions, so integer indexes and numeric strings are handled too.
/// Pure — no Uniconta types — so it can be unit tested on any platform.
/// </summary>
public static class UserFieldValueNormalizer
{
    /// <summary>
    /// Returns the canonical string value for an enum user field, or null when no value is selected.
    /// Handles: null/blank → null; int/short/long/byte index → value at that index (null when out of
    /// range); numeric string ("0", " 1 ") → value at that index; value string → trimmed, matched
    /// case-insensitively to canonical casing. Unknown strings are returned trimmed as-is so the
    /// validator can decide how to treat values it does not recognise.
    /// </summary>
    public static string? Normalize(object? raw, IReadOnlyList<string> valuesInIndexOrder)
    {
        switch (raw)
        {
            case null:
                return null;
            case byte b:
                return ValueAt(b, valuesInIndexOrder);
            case short s:
                return ValueAt(s, valuesInIndexOrder);
            case int i:
                return ValueAt(i, valuesInIndexOrder);
            case long l:
                return l >= int.MinValue && l <= int.MaxValue ? ValueAt((int)l, valuesInIndexOrder) : null;
            case string text:
                return NormalizeString(text, valuesInIndexOrder);
            default:
                return null;
        }
    }

    private static string? NormalizeString(string text, IReadOnlyList<string> valuesInIndexOrder)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        foreach (var value in valuesInIndexOrder)
        {
            if (string.Equals(trimmed, value, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        if (int.TryParse(trimmed, out var index))
            return ValueAt(index, valuesInIndexOrder);

        return trimmed;
    }

    private static string? ValueAt(int index, IReadOnlyList<string> valuesInIndexOrder)
        => index >= 0 && index < valuesInIndexOrder.Count ? valuesInIndexOrder[index] : null;
}

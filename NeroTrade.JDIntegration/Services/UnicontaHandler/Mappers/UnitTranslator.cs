namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

/// <summary>
/// Translates a Uniconta line unit into the JD container-type name it corresponds to.
///
/// Why this exists: the Uniconta SDK returns a line's unit as the <b>English</b> <c>ItemUnit</c> enum
/// name (<c>"Packages"</c>, <c>"Pcs"</c>, <c>"Pallet"</c>), while both the Uniconta UI and JD's container
/// types use the <b>Danish</b> label for the very same unit (<c>"Kolli"</c>, <c>"Stk"</c>, <c>"Palle"</c>).
/// JD resolves a line's container type by exact name, so the raw enum name never matched and every
/// product line silently degraded to the "Stk" default — observed on PO 33/37/39, all of which carry
/// <c>Unit = "Packages"</c> (Kolli) in Uniconta but were registered as Stk in JD.
///
/// Only the four units JD actually has container types for are translated. Anything else passes
/// through unchanged, which keeps the free-text container parent working: <c>xEnhedstype</c> is typed
/// by hand in Danish ("Palle") and already matches JD directly. A unit with no JD counterpart
/// (e.g. "kg", "Bag") also passes through and is reported by
/// <c>JdLogisticsService.SetContainerTypesAsync</c> before it falls back to Stk.
/// </summary>
public static class UnitTranslator
{
    // Uniconta ItemUnit enum name → JD container type name (= the Danish label Uniconta shows the user).
    // Verified against JD GET /api/containertypes on 2026-07-27: Container(1), Palle(3), Kolli(13), Stk(15).
    private static readonly Dictionary<string, string> UnicontaToJd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pcs"] = "Stk",
        ["Packages"] = "Kolli",
        ["Pallet"] = "Palle",
        ["Container"] = "Container"
    };

    /// <summary>
    /// Returns the JD container-type name for a Uniconta unit, or the trimmed input when there is no
    /// translation for it. Null/blank input returns null so the caller applies JD's default.
    /// </summary>
    public static string? ToJdContainerTypeName(string? unicontaUnit)
    {
        if (string.IsNullOrWhiteSpace(unicontaUnit)) return null;
        var trimmed = unicontaUnit.Trim();
        return UnicontaToJd.TryGetValue(trimmed, out var jdName) ? jdName : trimmed;
    }
}

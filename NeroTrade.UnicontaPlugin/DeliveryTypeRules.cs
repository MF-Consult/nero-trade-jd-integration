namespace NeroTrade.UnicontaPlugin;

/// <summary>
/// Pure rules that drive how xDeliveryType reacts to the chosen transport type, so the plugin
/// (UI side) and the unit tests share one source of truth. No Uniconta types — compiled into the
/// net9.0 test core as well. The plugin enforces nothing here; <see cref="SalesOrderJdValidator"/>
/// remains the save-time backstop.
/// </summary>
public static class DeliveryTypeRules
{
    /// <summary>
    /// True when xDeliveryType should be visible/selectable for the given transport type, i.e.
    /// only for "JD Logistik Transport". Every other transport type (including unknown / empty)
    /// hides the field. Inputs may be raw — comparison is trimmed and case-insensitive.
    /// </summary>
    public static bool ShouldShowDeliveryType(string? transportType)
        => Is(transportType, TransportTypeValues.JdLogistik);

    /// <summary>
    /// The value xDeliveryType should take when the transport type changes, or <c>null</c> to
    /// leave the field untouched (so we never write a no-op and re-trigger the change event):
    /// <list type="bullet">
    /// <item>JD Logistik + empty delivery type → default <see cref="DeliveryTypeValues.PalleFragt"/>
    /// (a value already set by the user is kept, GLS stays selectable).</item>
    /// <item>Ekstern Transport / Afhenter Selv + non-empty delivery type → "" (clear it, the field
    /// is hidden and must be empty at save time).</item>
    /// <item>anything else → <c>null</c>.</item>
    /// </list>
    /// Inputs may be raw — comparison is trimmed and case-insensitive.
    /// </summary>
    public static string? ResolveDeliveryTypeOnTransportChange(string? transportType, string? currentDeliveryType)
    {
        var hasDeliveryType = !string.IsNullOrWhiteSpace(currentDeliveryType);

        if (Is(transportType, TransportTypeValues.JdLogistik))
            return hasDeliveryType ? null : DeliveryTypeValues.PalleFragt;

        if (Is(transportType, TransportTypeValues.Ekstern) || Is(transportType, TransportTypeValues.AfhenterSelv))
            return hasDeliveryType ? "" : null;

        return null;
    }

    private static bool Is(string? actual, string expected)
        => string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

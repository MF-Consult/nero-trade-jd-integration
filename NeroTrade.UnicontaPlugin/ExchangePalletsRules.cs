namespace NeroTrade.UnicontaPlugin;

/// <summary>
/// Decides when the byttepaller (xByttepaller) field is relevant — i.e. when the order is a pallet
/// order that the integration could send <c>PL_EXCHANGE</c> for. Only then is the field shown and
/// the choice forced; for parcels / pickup it is hidden and not required.
/// Pure — no Uniconta types — so it is shared by the plugin (validation + show/hide) and the tests.
/// </summary>
public static class ExchangePalletsRules
{
    /// <summary>
    /// Mirrors the pallet detection in <c>SalesOrderMapper</c>: a pallet order is
    /// "Palle Fragt" (any transport type) OR an empty delivery type combined with
    /// "Ekstern Transport". Every other combination (GLS, Afhenter Selv, unknown) is not a pallet
    /// order, so byttepaller does not apply. Inputs may be raw — comparison is trimmed/ignore-case.
    /// </summary>
    public static bool IsRelevant(string? transportType, string? deliveryType)
    {
        if (Is(deliveryType, DeliveryTypeValues.PalleFragt))
            return true;

        if (string.IsNullOrWhiteSpace(deliveryType) && Is(transportType, TransportTypeValues.Ekstern))
            return true;

        return false;
    }

    private static bool Is(string? actual, string expected)
        => string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

namespace NeroTrade.UnicontaPlugin;

/// <summary>
/// Danish error messages shown to the user by the Uniconta client when a save is rejected.
/// Public constants so tests can assert the exact strings.
/// </summary>
public static class ValidationMessages
{
    public const string DeliveryDateMissing =
        "Udleveringsdato skal udfyldes ved overførsel til JD Logistik";

    public const string TrackingNoteMissing =
        "Sporingsnote (xTrackingNote) skal udfyldes ved overførsel til JD Logistik";

    public const string TransportTypeMissing =
        "Transporttype skal vælges ved overførsel til JD Logistik";

    public const string DeliveryTypeRequiredForJdLogistik =
        "Leveringstype (GLS / Palle Fragt) skal vælges når transporttype er 'JD Logistik Transport'";

    /// <summary>{0} = the chosen transport type.</summary>
    public const string DeliveryTypeMustBeEmptyFormat =
        "Leveringstype skal være tom når transporttype er '{0}'";
}

/// <summary>
/// Save-time validation of sales orders flagged for transfer to JD Logistik.
/// Pure — no Uniconta types — callers normalize enum user fields first
/// (see <see cref="UserFieldValueNormalizer"/>).
/// </summary>
public static class SalesOrderJdValidator
{
    /// <summary>
    /// Returns null when the save is allowed; otherwise a Danish error message that the
    /// Uniconta client shows to the user. Orders without the transfer flag are never
    /// validated (draft flow must stay untouched). Unknown transport types pass through —
    /// the plugin must never block saves on values it does not recognise.
    /// <paramref name="transportType"/> and <paramref name="deliveryType"/> must be passed
    /// through <see cref="UserFieldValueNormalizer.Normalize"/> first; a raw index value
    /// like "0" would otherwise be treated as an unknown transport type and pass through.
    /// </summary>
    public static string? Validate(
        bool transferToJd,
        DateTime deliveryDate,
        string? trackingNote,
        string? transportType,
        string? deliveryType)
    {
        if (!transferToJd)
            return null;

        if (deliveryDate == default)
            return ValidationMessages.DeliveryDateMissing;

        if (string.IsNullOrWhiteSpace(trackingNote))
            return ValidationMessages.TrackingNoteMissing;

        var transport = transportType?.Trim();
        if (string.IsNullOrEmpty(transport))
            return ValidationMessages.TransportTypeMissing;

        var hasDeliveryType = !string.IsNullOrWhiteSpace(deliveryType);

        if (Is(transport, TransportTypeValues.JdLogistik))
            return hasDeliveryType ? null : ValidationMessages.DeliveryTypeRequiredForJdLogistik;

        // xDeliveryType overrides the transport type in SalesOrderMapper, so a filled-in
        // delivery type on these transports would book the wrong carrier.
        // The message deliberately shows the canonical value-list name, never the raw input.
        if (Is(transport, TransportTypeValues.Ekstern))
            return hasDeliveryType
                ? string.Format(ValidationMessages.DeliveryTypeMustBeEmptyFormat, TransportTypeValues.Ekstern)
                : null;

        if (Is(transport, TransportTypeValues.AfhenterSelv))
            return hasDeliveryType
                ? string.Format(ValidationMessages.DeliveryTypeMustBeEmptyFormat, TransportTypeValues.AfhenterSelv)
                : null;

        return null;
    }

    private static bool Is(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}

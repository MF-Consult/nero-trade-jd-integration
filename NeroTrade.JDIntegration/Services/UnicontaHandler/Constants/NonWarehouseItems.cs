namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;

/// <summary>
/// Uniconta items that appear on purchase orders but are never physical goods — transport, print plates,
/// prepayments. JD is a warehouse and only knows catalog items, so a purchase line carrying one of these made
/// JD reject the whole incoming shipment (PO 26, 2026-09-29: "No matching JD catalog item for SKU(s):
/// PRINTPLA, Forud"). Lines whose item number <b>or</b> item name matches an entry are dropped before mapping,
/// on both the open-order and the posted-invoice path.
///
/// Static by agreement with Nero Trade (Maiwand, 2026-09-29): add an entry here when a new cost item shows up.
/// Matching is exact, case-insensitive, after trimming. The item number is the reliable key. The line text and the
/// item name are matched too, as a fallback for entries listed by name — but note that the
/// SDK's item-name getters depend on a client-side item cache (without it, verified 2026-09-29:
/// <c>CreditorOrderLineClient.Name</c> returns null, <c>CreditorInvoiceLines.ItemName</c> throws), so the name
/// match mainly rides on the line's stored text (<c>_Text</c> — read the raw field: the <c>Text</c> property falls
/// back to the item cache and throws without it). The stored text is only set when someone typed or kept a text
/// on the line, so a name entry is best-effort: add the item number whenever it is known.
/// </summary>
public static class NonWarehouseItems
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "PRINTPLA",                 // printplader
        "Forud",                    // forudbetalinger
        "1200",                     // Cargo - Internal (transport)
        "7",                        // Cargo Transport (transport)
        "Cargo - Internal",
        "Cargo Transport",
    };

    /// <param name="itemNumber">The line's item number (<c>_Item</c>) — the reliable key.</param>
    /// <param name="names">Secondary candidates: the line text and the item name. Either may be null.</param>
    public static bool IsExcluded(string? itemNumber, params string?[] names) =>
        Matches(itemNumber) || names.Any(Matches);

    private static bool Matches(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Excluded.Contains(value.Trim());
}

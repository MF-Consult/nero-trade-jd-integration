namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

/// <summary>
/// Allow-list of Shipmondo service codes per product code, derived from JD Logistik's
/// /api/shipmondo/carriers/{carrierCode}/products endpoint.
///
/// JD validates each entry in <see cref="Models.ExternalIntegration.JdRequestOrderShipmondo.productServices"/>
/// against the product's <c>availableServices</c>. Sending a service that the product does not list yields
/// <c>"&lt;code&gt; isn't an allowed service"</c> and the request is rejected.
/// </summary>
public static class ShipmondoProductCatalog
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> SupportedServicesPerProduct =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // GLS BusinessParcel — parcels only; available_services from JD: EMAIL_NT, SMS_NT.
            // Does NOT support PL_EXCHANGE or TIMED_DELIVERY.
            ["GLSDK_BP"] = new(StringComparer.OrdinalIgnoreCase),

            // Glimoe Standard pallets — available_services from JD: TIMED_DELIVERY, PL_EXCHANGE
            // (both flagged own_agreement_required, so a Glimoe agreement must be on file with JD).
            ["GLIMOE_PARCEL"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ShipmondoServiceCodes.PalletExchange,
                ShipmondoServiceCodes.TimedDelivery,
            },
        };

    public static bool SupportsService(string productCode, string serviceCode) =>
        SupportedServicesPerProduct.TryGetValue(productCode, out var services)
        && services.Contains(serviceCode);
}

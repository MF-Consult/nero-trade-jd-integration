namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

/// <summary>
/// Service codes sent in the Shipmondo part of a JD request order (<see cref="JdRequestOrderShipmondo.productServices"/>).
/// </summary>
public static class ShipmondoServiceCodes
{
    public const string TimedDelivery = "TIMED_DELIVERY";

    // Byttepaller — only valid for pallet products (e.g. GLIMOE_PARCEL).
    // JD rejects with "PL_EXCHANGE isn't an allowed service" if sent for parcel products like GLSDK_BP.
    // See ShipmondoProductCatalog for the per-product allow-list.
    public const string PalletExchange = "PL_EXCHANGE";
}

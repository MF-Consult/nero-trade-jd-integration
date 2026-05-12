namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

/// <summary>
/// Service codes sent in the Shipmondo part of a JD request order (<see cref="JdRequestOrderShipmondo.productServices"/>).
/// </summary>
public static class ShipmondoServiceCodes
{
    public const string TimedDelivery = "TIMED_DELIVERY";

    // Byttepaller — always sent to JD for Shipmondo-routed orders (confirmed by JD Logistik, 2026-05-12).
    public const string PalletExchange = "PL_EXCHANGE";
}

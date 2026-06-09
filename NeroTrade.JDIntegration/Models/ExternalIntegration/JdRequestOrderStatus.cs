namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

/// <summary>
/// JD Logistik request-order status values, mirroring the status enum in the JD API
/// (https://api.jdlogistik.dk/swagger). Stored as <c>int?</c> on <see cref="JdRequestOrder.status"/>.
/// Distinct from <see cref="JdRequestOrderStage"/>, which tracks fulfilment progress.
///
/// Note: a <see cref="Cancelled"/> order is terminal. JD does not hard-remove it — a DELETE returns
/// 204 but the order keeps appearing in the request-orders list — so cancelled orders must be treated
/// as absent when de-duplicating incoming sales orders, otherwise a re-upload is blocked.
/// </summary>
public static class JdRequestOrderStatus
{
    public const int Pending = 0;    // Afventer
    public const int Approved = 1;   // Godkendt
    public const int Denied = 2;     // Afvist
    public const int Cancelled = 3;  // Annulleret

    public static string Describe(int? status) => status switch
    {
        Pending => "Pending",
        Approved => "Approved",
        Denied => "Denied",
        Cancelled => "Cancelled",
        null => "(none)",
        _ => $"Unknown({status})",
    };
}

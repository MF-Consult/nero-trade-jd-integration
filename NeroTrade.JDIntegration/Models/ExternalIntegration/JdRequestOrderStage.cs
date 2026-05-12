namespace NeroTrade.JDIntegration.Models.ExternalIntegration;

/// <summary>
/// JD Logistik request-order stage values, mirroring the <c>ERequestOrderStage</c> enum in the
/// JD API (https://api.jdlogistik.dk/swagger). Stored as <c>int?</c> on <see cref="JdRequestOrder.stage"/>.
///
/// JD's documented deletion rule (DELETE /api/inventories/{inventoryid}/requestorders/{id}):
/// "If the RequestOrder is approved and pending dispatch it wont be possible."
/// In practice this means anything at <see cref="PendingDispatch"/> or beyond cannot be deleted.
/// </summary>
public static class JdRequestOrderStage
{
    public const int Pending = 0;
    public const int Denied = 1;
    public const int Planned = 2;
    public const int PendingDispatch = 3;
    public const int Dispatched = 4;
    public const int Closed = 90;

    /// <summary>
    /// True once the order has progressed past the point where JD will accept a delete:
    /// PendingDispatch (packed, awaiting pickup), Dispatched (sent), or Closed.
    /// </summary>
    public static bool IsPastDeletionThreshold(int? stage) =>
        stage is PendingDispatch or Dispatched or Closed;

    public static string Describe(int? stage) => stage switch
    {
        Pending => "Pending",
        Denied => "Denied",
        Planned => "Planned",
        PendingDispatch => "PendingDispatch",
        Dispatched => "Dispatched",
        Closed => "Closed",
        null => "(none)",
        _ => $"Unknown({stage})",
    };
}

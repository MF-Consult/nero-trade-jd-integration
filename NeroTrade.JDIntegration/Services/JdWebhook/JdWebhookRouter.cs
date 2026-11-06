namespace NeroTrade.JDIntegration.Services.JdWebhook;

/// <summary>
/// Maps a JD webhook type (<c>EWebhook</c>, from <c>GET /api/statics/enums</c>) to the scheduler job whose
/// work it makes urgent. A webhook is only a nudge: the existing polling job still does the actual reading, so
/// there is one code path for status/quantity sync and the slow poll doubles as reconciliation if JD ever
/// fails to deliver an event.
/// </summary>
public static class JdWebhookRouter
{
    // EWebhook values: 0 RequestOrder_Approved, 1 _Denied, 2 _PendingDispatch, 3 _Dispatched,
    // 10 Inventory_Updated, 20-22 Catalog_Created/Updated/Deleted, 30-32 IncomingShipment_Approved/Denied/Completed.
    public static string? JobFor(int webhookType) => webhookType switch
    {
        >= 0 and <= 3 => "RequestOrderStatus",
        >= 30 and <= 32 => "ReceivedQuantity",
        _ => null // inventory/catalog events are changes we made ourselves or that nothing reads
    };

    /// <summary>The webhook types the integration subscribes to.</summary>
    public static readonly int[] SubscribedTypes = [0, 1, 2, 3, 30, 31, 32];
}

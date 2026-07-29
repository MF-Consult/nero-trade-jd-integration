namespace NeroTrade.JDIntegration.Services.UnicontaHandler;

/// <summary>
/// Outcome of a write against an <b>open</b> purchase order.
///
/// <para>Exists to separate two things a plain <c>false</c> conflated: "Uniconta rejected the write" and
/// "there is no open order to write to". The second is the normal state of a purchase order that has been
/// booked — it leaves the open-order table and lives on as a posted invoice — and it is not a failure.</para>
///
/// <para>Treating it as one produced the single largest source of error rows in this integration: the
/// received-quantity sync retried a booked order every tick for the 24 hours its JD shipment stayed in the
/// lookback window, logging one <c>UNICONTA_CRUD_FAILED</c> per SKU per tick. Measured on 2026-07-29:
/// 85 rows for PO 34, ~85 for PO 10, 49 for PO 39, 25 for PO 35. Every one of them also reached the Hermes
/// agent, whose webhook filters on <c>level=eq.error</c>.</para>
/// </summary>
public enum UnicontaWriteResult
{
    /// <summary>The write was applied (or the value was already correct).</summary>
    Updated,

    /// <summary>
    /// No open purchase order with that number. Almost always means the order is booked. Callers should
    /// handle it as a state, not an error — see <c>SyncReceivedQuantityToUniconta</c>.
    /// </summary>
    OrderNotFound,

    /// <summary>The order (and line, where relevant) was found, but Uniconta rejected the write.</summary>
    Failed
}

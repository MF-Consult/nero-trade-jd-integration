using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// The single timer for the whole integration. Every 30 seconds it walks the six sync jobs in order and
/// lets each one decide, via <c>SyncScheduler.TryBeginRun</c>, whether it is due. Cadences are therefore
/// exactly what <c>SyncScheduling</c> config says they are — this class owns *when the heartbeat fires*,
/// nothing about how often a job runs.
///
/// <para><b>Why one timer instead of six.</b> Measurement on 2026-07-28 showed the Functions scale
/// controller places each timer trigger's listener on its own instance: six timer functions ⇒ six
/// instances, confirmed 1:1 by correlating <c>AppRoleInstance</c> with the job that ran on it, and
/// unaffected by capping <c>maximumInstanceCount</c> to 1. Each instance holds its own
/// <c>UnicontaConnectionManager</c> and therefore its own Uniconta session, used by exactly one job — so
/// the session reliably expired between that job's runs and had to be rebuilt. Login + OpenCompany is two
/// Uniconta calls, and those handshakes grew to ~56% of the daily call budget, which is capped by
/// agreement. The observed cost per run fit <c>1 + 2/ceil(sessionAge / interval)</c> exactly: 1.67 calls
/// for the 60 s job, 2.00 for the 90 s jobs, 3.00 for the 10-minute ones.</para>
///
/// With one timer there is one instance and one session shared by all six jobs. Their combined rhythm —
/// a run roughly every 25 s — keeps that session alive across many runs instead of letting it die between
/// each, which is what takes the projected volume from ~4.600 to ~3.300 calls/day without slowing anything
/// down.
///
/// <para><b>Trade-offs, deliberately accepted.</b> Jobs run sequentially, so a slow one delays the rest of
/// that tick (bounded by the per-call Uniconta timeout added 2026-07-27, and the whole cycle normally
/// finishes in well under a second). A tick that overruns 30 s simply means the timer skips — timer
/// triggers do not overlap themselves. App Insights now sees one function name instead of six; per-job
/// visibility lives where it already did, in the <c>integration_logs</c> run rows.</para>
/// </summary>
public sealed class SyncDispatcher(
    SyncSalesOrdersToJd salesOrders,
    SyncPurchaseOrdersToJd purchaseOrders,
    SyncPostedPurchaseInvoicesToJd postedPurchaseInvoices,
    SyncItemsToJd items,
    SyncRequestOrderStatusToUniconta requestOrderStatus,
    SyncReceivedQuantityToUniconta receivedQuantity,
    ILogger<SyncDispatcher> logger)
{
    [Function("SyncDispatcher")]
    public async Task RunAsync([TimerTrigger("*/30 * * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        // Order matters only in that the two order paths go first: they are the ones a user is waiting on.
        var jobs = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("SyncSalesOrdersToJd", salesOrders.RunAsync),
            ("SyncPurchaseOrdersToJd", purchaseOrders.RunAsync),
            ("SyncPostedPurchaseInvoicesToJd", postedPurchaseInvoices.RunAsync),
            ("SyncItemsToJd", items.RunAsync),
            ("SyncRequestOrderStatusToUniconta", requestOrderStatus.RunAsync),
            ("SyncReceivedQuantityToUniconta", receivedQuantity.RunAsync),
        };

        List<Exception>? failures = null;

        foreach (var (name, run) in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await run(cancellationToken);
            }
            catch (Exception ex)
            {
                // One failing job must not cost the others their tick — that would turn a single sync's
                // problem into an outage of all six. Each job has already written its own error row with
                // its own error_code, so the detail is not lost here.
                (failures ??= []).Add(ex);
                logger.LogError(ex, "Sync job {Job} failed inside the dispatcher; continuing with the rest", name);
            }
        }

        // Rethrow after the loop so the invocation is still marked failed: the App Insights
        // "function failure rate" alert is built on that, and swallowing would silence it.
        if (failures is { Count: > 0 })
            throw failures.Count == 1 ? failures[0] : new AggregateException("One or more sync jobs failed", failures);
    }
}

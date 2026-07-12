using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.Scheduling;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Safety-net for purchase orders that were booked ("bogført") before a user clicked "Overfør til JD"
/// on the open order. Once booked, the order leaves the open-order table that <see cref="SyncPurchaseOrdersToJd"/>
/// reads, so those goods would never register at JD. This scans recently-posted purchase invoices that
/// carry the same transfer flag (a user can still set it on the booked invoice) and sends the missed ones.
///
/// Idempotency is provided entirely by JD's existing-shipment dedup in
/// <see cref="IJdLogisticsService.CreateIncomingShipmentsAsync"/>: both this function and the open-order
/// function emit the identity "PO {originatingOrderNumber}", so an order already sent — by the open-order
/// flow or an earlier tick — is skipped, never duplicated. The Uniconta write-back is best-effort and only
/// gives the user UI feedback (JD Status → Oprettet); correctness does not depend on it.
/// </summary>
public sealed class SyncPostedPurchaseInvoicesToJd(
    IUnicontaService uniconta,
    PurchaseOrderMapper mapper,
    IJdLogisticsService jd,
    SyncScheduler scheduler,
    IIntegrationLogger integrationLogger,
    ILogger<SyncPostedPurchaseInvoicesToJd> logger)
{
    // Heartbeat only — the real day/night cadence is enforced by SyncScheduler ("PostedPurchaseInvoices"
    // cadence in SyncScheduling config). Non-due ticks return before any Uniconta call.
    [Function("SyncPostedPurchaseInvoicesToJd")]
    public async Task RunAsync([TimerTrigger("0 * * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (!scheduler.TryBeginRun("PostedPurchaseInvoices", DateTime.UtcNow)) return;

        await using var run = integrationLogger.BeginRun("SyncPostedPurchaseInvoicesToJd");
        var logScope = run.Scope;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = logScope.CorrelationId });
        logger.LogInformation("SyncPostedPurchaseInvoicesToJd started");

        try
        {
            var batch = new List<JdIncomingShipmentCreate>(capacity: 200);
            int totalProcessed = 0, totalSucceeded = 0, totalFailed = 0;
            await foreach (var invoice in uniconta.ReadPostedPurchaseInvoicesBatchedAsync(200, cancellationToken))
            {
                var payload = mapper.Map(invoice);
                payload.text = $"PO {invoice.PurchaseNumber}";
                batch.Add(payload);
                if (batch.Count >= 200)
                {
                    var (p, s, f) = await HandleBatchAsync(batch, logScope, cancellationToken);
                    totalProcessed += p; totalSucceeded += s; totalFailed += f;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                var (p, s, f) = await HandleBatchAsync(batch, logScope, cancellationToken);
                totalProcessed += p; totalSucceeded += s; totalFailed += f;
                batch.Clear();
            }

            logger.LogInformation("SyncPostedPurchaseInvoicesToJd completed. Total={Total}", totalProcessed);

            if (totalProcessed > 0)
            {
                run.AttachCompletionPayload(new { processed = totalProcessed, succeeded = totalSucceeded, failed = totalFailed });
            }
        }
        catch (Exception ex)
        {
            run.MarkFailed(ex);
            logger.LogError(ex, "SyncPostedPurchaseInvoicesToJd failed");
            throw;
        }
    }

    private async Task<(int processed, int succeeded, int failed)> HandleBatchAsync(List<JdIncomingShipmentCreate> batch, IntegrationLogScope logScope, CancellationToken ct)
    {
        var result = await jd.CreateIncomingShipmentsAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => f.Item.text));
            logger.LogWarning("Posted-invoice incoming shipments create failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }

        // Created (or already present) in JD: best-effort mark the posted invoice Oprettet and consume the
        // flag. If the write-back fails (posted invoices may be immutable), idempotency still holds via JD
        // dedup — the invoice just stays eligible until it ages out of the lookback window.
        foreach (var item in result.CreatedItems)
        {
            if (!item.SourcePurchaseNumber.HasValue) continue;
            var ok = await uniconta.SetPurchaseInvoiceHeaderFieldsAsync(item.SourcePurchaseNumber.Value, new Dictionary<string, object>
            {
                [UnicontaUserFields.PurchaseOrderJdStatus] = PurchaseOrderJdStatusValues.Created,
                [UnicontaUserFields.PurchaseOrderTransferFlag] = false,
            }, ct);

            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "info", "Integration", item.SourcePurchaseNumber.Value.ToString(),
                $"Posted purchase invoice for order {item.SourcePurchaseNumber} synced to JD (safety-net).", null, null)
            {
                CorrelationId = logScope.CorrelationId
            }, ct);
            await integrationLogger.MarkResolvedAsync(
                integrationLogger.IntegrationName,
                item.SourcePurchaseNumber.Value.ToString(),
                logScope.CorrelationId,
                ct);

            if (!ok)
            {
                logger.LogWarning("Posted invoice for order {Po} sent to JD but the Uniconta status write-back failed (posted invoice may be read-only); JD dedup keeps it idempotent.", item.SourcePurchaseNumber.Value);
            }
        }

        // Rejected by JD: best-effort park the invoice for manual handling and consume the flag.
        foreach (var failure in result.Failures)
        {
            if (!failure.Item.SourcePurchaseNumber.HasValue) continue;
            var parked = await uniconta.SetPurchaseInvoiceHeaderFieldsAsync(failure.Item.SourcePurchaseNumber.Value, new Dictionary<string, object>
            {
                [UnicontaUserFields.PurchaseOrderJdStatus] = PurchaseOrderJdStatusValues.ManualHandling,
                [UnicontaUserFields.PurchaseOrderTransferFlag] = false,
            }, ct);
            if (!parked)
            {
                // Best-effort: if the posted invoice is read-only the flag stays set and this invoice
                // will be re-attempted (and re-rejected) each tick until it ages out of the lookback window.
                logger.LogWarning("Posted invoice for order {Po} was rejected by JD and could not be parked (Manuel handling) in Uniconta; it may re-attempt until it leaves the lookback window.", failure.Item.SourcePurchaseNumber.Value);
            }

            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "error", "JD", failure.Item.SourcePurchaseNumber.Value.ToString(),
                $"JD rejected posted purchase invoice for order {failure.Item.SourcePurchaseNumber}: {LogSanitizer.Sanitize(failure.Message)}", null,
                JsonSerializer.SerializeToElement(new { errorMessage = failure.Message, sourcePurchaseNumber = failure.Item.SourcePurchaseNumber }))
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "JD_VALIDATION_REJECTED",
                Retryable = false,
                SuggestedAction = "Manual review — posted invoice marked Manuel handling. Fix the JD catalog item, then re-set Overfør til JD on the invoice."
            }, ct);
        }

        logger.LogInformation("Posted-invoice JD batch: created_or_exists={Created} failures={Failures}", result.SuccessCount, result.Failures.Count);
        return (batch.Count, result.SuccessCount, result.Failures.Count);
    }
}

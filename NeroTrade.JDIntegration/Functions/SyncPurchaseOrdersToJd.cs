using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncPurchaseOrdersToJd(
    IUnicontaService uniconta,
    PurchaseOrderMapper mapper,
    IJdLogisticsService jd,
    IIntegrationLogger integrationLogger,
    ILogger<SyncPurchaseOrdersToJd> logger)
{
    [Function("SyncPurchaseOrdersToJd")]
    public async Task RunAsync([TimerTrigger("0 */1 * * * *")] TimerInfo timer)
    {
        await using var run = integrationLogger.BeginRun("SyncPurchaseOrdersToJd");
        var logScope = run.Scope;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = logScope.CorrelationId });
        logger.LogInformation("SyncPurchaseOrdersToJd started");

        try
        {
            var batch = new List<JdIncomingShipmentCreate>(capacity: 200);
            int totalProcessed = 0, totalSucceeded = 0, totalFailed = 0;
            await foreach (var po in uniconta.ReadPurchaseOrdersBatchedAsync(200, cancellationToken))
            {
                var payload = mapper.Map(po);
                payload.text = $"PO {po.PurchaseNumber}";
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

            logger.LogInformation("SyncPurchaseOrdersToJd completed. Total={Total}", totalProcessed);

            if (totalProcessed > 0)
            {
                run.AttachCompletionPayload(new { processed = totalProcessed, succeeded = totalSucceeded, failed = totalFailed });
            }
        }
        catch (Exception ex)
        {
            run.MarkFailed(ex);
            logger.LogError(ex, "SyncPurchaseOrdersToJd failed");
            throw;
        }
    }

    private async Task<(int processed, int succeeded, int failed)> HandleBatchAsync(List<JdIncomingShipmentCreate> batch, IntegrationLogScope logScope, CancellationToken ct)
    {
        var result = await jd.CreateIncomingShipmentsAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => f.Item.text));
            logger.LogWarning("Incoming shipments create failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }

        // Created (or already present) in JD: status = Oprettet, and consume the transfer trigger.
        int markedCreated = 0;
        foreach (var item in result.CreatedItems)
        {
            if (!item.SourcePurchaseNumber.HasValue) continue;
            var ok = await uniconta.SetPurchaseOrderHeaderFieldsAsync(item.SourcePurchaseNumber.Value, new Dictionary<string, object>
            {
                [UnicontaUserFields.PurchaseOrderJdStatus] = PurchaseOrderJdStatusValues.Created,
                [UnicontaUserFields.PurchaseOrderTransferFlag] = false,
            }, ct);
            if (ok)
            {
                markedCreated++;
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    integrationLogger.IntegrationName, "info", "Integration", item.SourcePurchaseNumber.Value.ToString(),
                    $"Purchase order {item.SourcePurchaseNumber} synced to JD.", null, null)
                {
                    CorrelationId = logScope.CorrelationId
                }, ct);
                await integrationLogger.MarkResolvedAsync(
                    integrationLogger.IntegrationName,
                    item.SourcePurchaseNumber.Value.ToString(),
                    logScope.CorrelationId,
                    ct);
            }
            else
            {
                logger.LogError("Failed to set PO {Po} status to Oprettet in Uniconta", item.SourcePurchaseNumber.Value);
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    integrationLogger.IntegrationName, "warning", "Uniconta", item.SourcePurchaseNumber.Value.ToString(),
                    $"Purchase order {item.SourcePurchaseNumber} was sent to JD but the Uniconta status update failed; will retry next run.",
                    null, null)
                {
                    CorrelationId = logScope.CorrelationId,
                    ErrorCode = "UNICONTA_CRUD_FAILED",
                    Retryable = true,
                    SuggestedAction = "Auto-recovers on next tick; if persistent, retry PO via /admin/retry-purchase-order."
                }, ct);
            }
        }

        // Rejected by JD: park for manual handling and consume the transfer trigger. It will only be
        // retried once a user sets xTransferToJD again (status is then "Manuel handling" or empty).
        int markedManual = 0;
        foreach (var failure in result.Failures)
        {
            if (!failure.Item.SourcePurchaseNumber.HasValue) continue;
            var ok = await uniconta.SetPurchaseOrderHeaderFieldsAsync(failure.Item.SourcePurchaseNumber.Value, new Dictionary<string, object>
            {
                [UnicontaUserFields.PurchaseOrderJdStatus] = PurchaseOrderJdStatusValues.ManualHandling,
                [UnicontaUserFields.PurchaseOrderTransferFlag] = false,
            }, ct);
            if (ok) markedManual++;
            else logger.LogError("Failed to set PO {Po} status to Manuel handling in Uniconta", failure.Item.SourcePurchaseNumber.Value);

            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "error", "JD", failure.Item.SourcePurchaseNumber.Value.ToString(),
                $"JD rejected purchase order {failure.Item.SourcePurchaseNumber}: {LogSanitizer.Sanitize(failure.Message)}", null,
                JsonSerializer.SerializeToElement(new { errorMessage = failure.Message, sourcePurchaseNumber = failure.Item.SourcePurchaseNumber }))
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "JD_VALIDATION_REJECTED",
                Retryable = false,
                SuggestedAction = "Manual review — PO marked Manuel handling in Uniconta with the JD reject reason."
            }, ct);
        }

        logger.LogInformation("JD incoming shipments batch: created_or_exists={Created} marked_oprettet={MarkedCreated} failures={Failures} marked_manuel={MarkedManual}",
            result.SuccessCount, markedCreated, result.Failures.Count, markedManual);

        return (batch.Count, result.SuccessCount, result.Failures.Count);
    }
}
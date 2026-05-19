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
    SupabaseOptions supabaseOptions,
    ILogger<SyncPurchaseOrdersToJd> logger)
{
    [Function("SyncPurchaseOrdersToJd")]
    public async Task RunAsync([TimerTrigger("*/40 * * * * *")] TimerInfo timer)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncPurchaseOrdersToJd started");

        try
        {
            var batch = new List<JdIncomingShipmentCreate>(capacity: 200);
            int totalProcessed = 0, totalSucceeded = 0, totalFailed = 0;
            await foreach (var po in uniconta.ReadPurchaseOrdersBatchedAsync(200, cts.Token))
            {
                var payload = mapper.Map(po);
                payload.text = $"PO {po.PurchaseNumber}";
                batch.Add(payload);
                if (batch.Count >= 200)
                {
                    var (p, s, f) = await HandleBatchAsync(batch, cts.Token);
                    totalProcessed += p; totalSucceeded += s; totalFailed += f;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                var (p, s, f) = await HandleBatchAsync(batch, cts.Token);
                totalProcessed += p; totalSucceeded += s; totalFailed += f;
                batch.Clear();
            }

            logger.LogInformation("SyncPurchaseOrdersToJd completed. Total={Total}", totalProcessed);

            if (totalProcessed > 0)
            {
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "info", "Integration", null,
                    $"SyncPurchaseOrdersToJd completed: {totalSucceeded} succeeded, {totalFailed} failed.",
                    null,
                    JsonSerializer.SerializeToElement(new { processed = totalProcessed, succeeded = totalSucceeded, failed = totalFailed })
                ), cts.Token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncPurchaseOrdersToJd failed");
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncPurchaseOrdersToJd run failed: {ex.Message}", ex.ToString(), null
            ), CancellationToken.None);
            throw;
        }
    }

    private async Task<(int processed, int succeeded, int failed)> HandleBatchAsync(List<JdIncomingShipmentCreate> batch, CancellationToken ct)
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
                    supabaseOptions.IntegrationName, "info", "Integration", item.SourcePurchaseNumber.Value.ToString(),
                    $"Purchase order {item.SourcePurchaseNumber} synced to JD.", null, null), ct);
            }
            else
            {
                logger.LogError("Failed to set PO {Po} status to Oprettet in Uniconta", item.SourcePurchaseNumber.Value);
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "warning", "Uniconta", item.SourcePurchaseNumber.Value.ToString(),
                    $"Purchase order {item.SourcePurchaseNumber} was sent to JD but the Uniconta status update failed; will retry next run.",
                    null, null), ct);
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
                supabaseOptions.IntegrationName, "error", "JD", failure.Item.SourcePurchaseNumber.Value.ToString(),
                $"JD rejected purchase order {failure.Item.SourcePurchaseNumber}: {failure.Message}", null,
                JsonSerializer.SerializeToElement(new { errorMessage = failure.Message, sourcePurchaseNumber = failure.Item.SourcePurchaseNumber })), ct);
        }

        logger.LogInformation("JD incoming shipments batch: created_or_exists={Created} marked_oprettet={MarkedCreated} failures={Failures} marked_manuel={MarkedManual}",
            result.SuccessCount, markedCreated, result.Failures.Count, markedManual);

        return (batch.Count, result.SuccessCount, result.Failures.Count);
    }
}
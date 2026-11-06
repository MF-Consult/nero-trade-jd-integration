using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.Scheduling;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncPurchaseOrdersToJd(
    IUnicontaService uniconta,
    PurchaseOrderMapper mapper,
    ItemMapper itemMapper,
    PurchaseOrderSyncOptions options,
    IJdLogisticsService jd,
    SyncScheduler scheduler,
    IIntegrationLogger integrationLogger,
    ILogger<SyncPurchaseOrdersToJd> logger)
{
    // Invoked by SyncDispatcher, not by its own timer — see that class. The scheduler gate below
    // still decides whether this tick does any work, so the configured cadence is unchanged.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!scheduler.TryBeginRun("PurchaseOrders", DateTime.UtcNow)) return;

        await using var run = integrationLogger.BeginRun("SyncPurchaseOrdersToJd");
        var logScope = run.Scope;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = logScope.CorrelationId });
        logger.LogInformation("SyncPurchaseOrdersToJd started");

        try
        {
            var batch = new List<JdIncomingShipmentCreate>(capacity: 200);
            var flaggedAt = new Dictionary<int, DateTime>();
            int totalProcessed = 0, totalSucceeded = 0, totalFailed = 0;
            await foreach (var po in uniconta.ReadPurchaseOrdersBatchedAsync(200, cancellationToken))
            {
                var payload = mapper.Map(po);
                if (po.UpdatedAt.HasValue) flaggedAt[po.PurchaseNumber] = po.UpdatedAt.Value;
                batch.Add(payload);
                if (batch.Count >= 200)
                {
                    var (p, s, f) = await HandleBatchAsync(batch, flaggedAt, logScope, cancellationToken);
                    totalProcessed += p; totalSucceeded += s; totalFailed += f;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                var (p, s, f) = await HandleBatchAsync(batch, flaggedAt, logScope, cancellationToken);
                totalProcessed += p; totalSucceeded += s; totalFailed += f;
                batch.Clear();
            }

            logger.LogInformation("SyncPurchaseOrdersToJd completed. Total={Total}", totalProcessed);

            if (totalProcessed > 0)
            {
                run.AttachCompletionPayload(new { processed = totalProcessed, succeeded = totalSucceeded, failed = totalFailed });
            }
        }
        catch (UnicontaConnectionUnavailableException ex)
        {
            // Uniconta was unreachable this tick — nothing actionable on our side, and the next
            // tick reconnects. Recorded as a warning run (see ErrorCodeClassifier) and deliberately
            // NOT rethrown, so it does not also surface as a failed invocation in App Insights.
            run.MarkFailed(ex);
            run.ExitReason = "uniconta_unavailable";
            logger.LogWarning(ex, "Uniconta unavailable; skipping this SyncPurchaseOrdersToJd tick");
        }
        catch (Exception ex)
        {
            run.MarkFailed(ex);
            logger.LogError(ex, "SyncPurchaseOrdersToJd failed");
            throw;
        }
    }

    /// <summary>
    /// End-to-end delay for a purchase order: Uniconta's last-modified time on the header (a user ticking
    /// <c>xTransferToJD</c> bumps it) to now. Logged on the success row so "it took too long" can be
    /// answered from <c>integration_logs</c> instead of guessed. Null when Uniconta gave no timestamp.
    /// </summary>
    internal static JsonElement? DelayPayload(IReadOnlyDictionary<int, DateTime> flaggedAt, int purchaseNumber, DateTime? nowUtc = null)
    {
        if (!flaggedAt.TryGetValue(purchaseNumber, out var flagged)) return null;
        var now = nowUtc ?? DateTime.UtcNow;
        var flaggedUtc = DateTime.SpecifyKind(flagged, DateTimeKind.Utc);
        return JsonSerializer.SerializeToElement(new
        {
            flaggedAtUtc = flaggedUtc,
            sentAtUtc = now,
            delaySeconds = (int)Math.Max(0, (now - flaggedUtc).TotalSeconds)
        });
    }

    /// <summary>
    /// Creates, ahead of the shipment, the JD catalog items a purchase order needs but JD does not have yet.
    /// The flagged-item sync (<c>XoverforVare</c>) is the normal route; this closes the gap when a user sends
    /// an order for an item whose flag was never set, which used to reject the order ("No matching JD catalog
    /// item") and leave the customer waiting for a transfer that could not happen. Only runs when
    /// <see cref="PurchaseOrderSyncOptions.AutoCreateMissingItems"/> is on. Anything it cannot fix (item missing
    /// in Uniconta, JD refusing the payload) is logged and the order is rejected exactly as before.
    /// </summary>
    private async Task EnsureCatalogItemsAsync(List<JdIncomingShipmentCreate> batch, IntegrationLogScope logScope, CancellationToken ct)
    {
        var missing = await jd.FindMissingCatalogSkusAsync(batch, ct);
        if (missing.Count == 0) return;

        var found = await uniconta.ReadItemsBySkusAsync(missing, ct);
        var foundSkus = found.Select(i => i.Sku?.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in missing.Where(m => !foundSkus.Contains(m)))
        {
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "warning", "Uniconta", sku,
                $"Item {sku} is on a purchase order but exists neither in JD nor in the Uniconta item master; it cannot be created in JD.", null, null)
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "UNICONTA_LOOKUP_MISS",
                Retryable = false,
                SuggestedAction = "Check the item number on the purchase order line; create the item in Uniconta."
            }, ct);
        }

        var catalogItems = found.Select(itemMapper.Map).Where(i => !string.IsNullOrWhiteSpace(i.sku)).ToList();
        if (catalogItems.Count == 0) return;

        var result = await jd.UpsertItemsAsync(catalogItems, ct);
        foreach (var created in result.CreatedItems)
        {
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "info", "Integration", created.sku,
                $"Catalog item {created.sku} auto-created in JD because a purchase order needed it.", null, null)
            {
                CorrelationId = logScope.CorrelationId
            }, ct);
        }
        foreach (var failure in result.Failures)
        {
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "error", "JD", failure.Item.sku,
                $"JD rejected auto-created catalog item {failure.Item.sku}: {LogSanitizer.Sanitize(failure.Message)}", null,
                JsonSerializer.SerializeToElement(new { status = failure.Status, message = failure.Message, sku = failure.Item.sku }))
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "JD_VALIDATION_REJECTED",
                Retryable = false,
                SuggestedAction = "Complete the item master data in Uniconta (name, weight, country) and resend the purchase order."
            }, ct);
        }
    }

    private async Task<(int processed, int succeeded, int failed)> HandleBatchAsync(List<JdIncomingShipmentCreate> batch, IReadOnlyDictionary<int, DateTime> flaggedAt, IntegrationLogScope logScope, CancellationToken ct)
    {
        if (options.AutoCreateMissingItems)
            await EnsureCatalogItemsAsync(batch, logScope, ct);

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
            var ok = UnicontaWriteResult.Updated == await uniconta.SetPurchaseOrderHeaderFieldsAsync(item.SourcePurchaseNumber.Value, new Dictionary<string, object>
            {
                [UnicontaUserFields.PurchaseOrderJdStatus] = PurchaseOrderJdStatusValues.Created,
                [UnicontaUserFields.PurchaseOrderTransferFlag] = false,
            }, ct);
            if (ok)
            {
                markedCreated++;
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    integrationLogger.IntegrationName, "info", "Integration", item.SourcePurchaseNumber.Value.ToString(),
                    $"Purchase order {item.SourcePurchaseNumber} synced to JD.", null,
                    DelayPayload(flaggedAt, item.SourcePurchaseNumber.Value))
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
            var ok = UnicontaWriteResult.Updated == await uniconta.SetPurchaseOrderHeaderFieldsAsync(failure.Item.SourcePurchaseNumber.Value, new Dictionary<string, object>
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
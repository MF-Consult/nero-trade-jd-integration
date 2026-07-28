using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.Scheduling;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncItemsToJd(
    IUnicontaService uniconta,
    ItemMapper mapper,
    IJdLogisticsService jd,
    SyncScheduler scheduler,
    IIntegrationLogger integrationLogger,
    ILogger<SyncItemsToJd> logger)
{
    // Invoked by SyncDispatcher, not by its own timer — see that class. The scheduler gate below
    // still decides whether this tick does any work, so the configured cadence is unchanged.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!scheduler.TryBeginRun("Items", DateTime.UtcNow)) return;

        await using var run = integrationLogger.BeginRun("SyncItemsToJd");
        var logScope = run.Scope;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = logScope.CorrelationId });
        logger.LogInformation("SyncItemsToJd started");

        try
        {
            var batch = new List<JdCatalogItem>(capacity: 200);
            int totalPrepared = 0, totalSucceeded = 0, totalFailed = 0;
            await foreach (var item in uniconta.ReadItemsBatchedAsync(200, cancellationToken))
            {
                var jdItem = mapper.Map(item);
                if (string.IsNullOrWhiteSpace(jdItem.sku)) continue;
                batch.Add(jdItem);
                if (batch.Count >= 200)
                {
                    var (p, s, f) = await HandleBatchAsync(batch, logScope, cancellationToken);
                    totalPrepared += p; totalSucceeded += s; totalFailed += f;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                var (p, s, f) = await HandleBatchAsync(batch, logScope, cancellationToken);
                totalPrepared += p; totalSucceeded += s; totalFailed += f;
                batch.Clear();
            }

            logger.LogInformation("SyncItemsToJd completed. TotalPrepared={Total}", totalPrepared);

            if (totalPrepared > 0)
            {
                run.AttachCompletionPayload(new { prepared = totalPrepared, succeeded = totalSucceeded, failed = totalFailed });
            }
        }
        catch (UnicontaConnectionUnavailableException ex)
        {
            // Uniconta was unreachable this tick — nothing actionable on our side, and the next
            // tick reconnects. Recorded as a warning run (see ErrorCodeClassifier) and deliberately
            // NOT rethrown, so it does not also surface as a failed invocation in App Insights.
            run.MarkFailed(ex);
            run.ExitReason = "uniconta_unavailable";
            logger.LogWarning(ex, "Uniconta unavailable; skipping this SyncItemsToJd tick");
        }
        catch (Exception ex)
        {
            run.MarkFailed(ex);
            logger.LogError(ex, "SyncItemsToJd failed");
            throw;
        }
    }

    private async Task<(int prepared, int succeeded, int failed)> HandleBatchAsync(List<JdCatalogItem> batch, IntegrationLogScope logScope, CancellationToken ct)
    {
        var result = await jd.UpsertItemsAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => $"{f.Item.sku}:{f.Status}"));
            logger.LogWarning("JD item upsert failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }

        // Only log per-item successes for NEW items (created), not updates. Updates fire every
        // 5-min tick because xOverforVare stays set, and a per-update row would be pure noise.
        // The "new item landed in JD" signal — which "varen kom ikke frem"-investigations need —
        // is captured by the create rows alone.
        foreach (var created in result.CreatedItems)
        {
            if (string.IsNullOrWhiteSpace(created.sku)) continue;
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "info", "Integration", created.sku,
                $"Catalog item {created.sku} created in JD.", null, null)
            {
                CorrelationId = logScope.CorrelationId
            }, ct);
            await integrationLogger.MarkResolvedAsync(
                integrationLogger.IntegrationName,
                created.sku,
                logScope.CorrelationId,
                ct);
        }

        foreach (var failure in result.Failures)
        {
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "error", "JD", failure.Item.sku,
                $"JD rejected catalog item {failure.Item.sku}: {LogSanitizer.Sanitize(failure.Message)}", null,
                JsonSerializer.SerializeToElement(new { status = failure.Status, message = failure.Message, sku = failure.Item.sku }))
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "JD_VALIDATION_REJECTED",
                Retryable = false,
                SuggestedAction = "Inspect item mapping — JD rejected the catalog payload."
            }, ct);
        }

        logger.LogInformation("JD item upsert batch success={Success} (created={Created}) failures={Failures}",
            result.SuccessCount, result.CreatedItems.Count, result.Failures.Count);
        return (batch.Count, result.SuccessCount, result.Failures.Count);
    }
}
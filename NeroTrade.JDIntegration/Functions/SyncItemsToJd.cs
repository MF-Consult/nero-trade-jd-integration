using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncItemsToJd(
    IUnicontaService uniconta,
    ItemMapper mapper,
    IJdLogisticsService jd,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<SyncItemsToJd> logger)
{
    [Function("SyncItemsToJd")]
    public async Task RunAsync([TimerTrigger("0 */2 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var logScope = new IntegrationLogScope();
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
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "info", "Integration", null,
                    $"SyncItemsToJd completed: {totalSucceeded} succeeded, {totalFailed} failed.",
                    null,
                    JsonSerializer.SerializeToElement(new { prepared = totalPrepared, succeeded = totalSucceeded, failed = totalFailed }))
                {
                    CorrelationId = logScope.CorrelationId
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncItemsToJd failed");
            var classified = ErrorCodeClassifier.Classify(ex);
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncItemsToJd run failed: {LogSanitizer.Describe(ex)}", null, null)
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = classified.ErrorCode,
                Retryable = classified.Retryable,
                SuggestedAction = classified.SuggestedAction
            }, CancellationToken.None);
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

        foreach (var failure in result.Failures)
        {
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "JD", failure.Item.sku,
                $"JD rejected catalog item {failure.Item.sku}: {LogSanitizer.Sanitize(failure.Message)}", null,
                JsonSerializer.SerializeToElement(new { status = failure.Status, message = failure.Message, sku = failure.Item.sku }))
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "JD_VALIDATION_REJECTED",
                Retryable = false,
                SuggestedAction = "Inspect item mapping — JD rejected the catalog payload."
            }, ct);
        }

        logger.LogInformation("JD item upsert batch success={Success} failures={Failures}", result.SuccessCount, result.Failures.Count);
        return (batch.Count, result.SuccessCount, result.Failures.Count);
    }
}
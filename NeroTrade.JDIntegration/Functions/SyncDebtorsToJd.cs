using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncDebtorsToJd(
    IUnicontaService uniconta,
    DebtorMapper mapper,
    IJdLogisticsService jd,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<SyncDebtorsToJd> logger)
{
    [Function("SyncDebtorsToJd")]
    public async Task RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", Route = "sync-debtors-to-jd")] HttpRequestData req)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncDebtorsToJd started");

        try
        {
            var jdBatch = new List<JdAddress>(capacity: 200);
            int totalPrepared = 0, totalSucceeded = 0, totalFailed = 0;
            await foreach (var debtor in uniconta.ReadDebtorsBatchedAsync(200, cts.Token))
            {
                jdBatch.Add(mapper.Map(debtor));
                if (jdBatch.Count >= 200)
                {
                    var (p, s, f) = await HandleBatchAsync(jdBatch, cts.Token);
                    totalPrepared += p; totalSucceeded += s; totalFailed += f;
                    jdBatch.Clear();
                }
            }
            if (jdBatch.Count > 0)
            {
                var (p, s, f) = await HandleBatchAsync(jdBatch, cts.Token);
                totalPrepared += p; totalSucceeded += s; totalFailed += f;
                jdBatch.Clear();
            }

            logger.LogInformation("SyncDebtorsToJd completed. TotalPrepared={Total}", totalPrepared);

            if (totalPrepared > 0)
            {
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "info", "Integration", null,
                    $"SyncDebtorsToJd completed: {totalSucceeded} succeeded, {totalFailed} failed.",
                    null,
                    JsonSerializer.SerializeToElement(new { prepared = totalPrepared, succeeded = totalSucceeded, failed = totalFailed })
                ), cts.Token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncDebtorsToJd failed");
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncDebtorsToJd run failed: {ex.Message}", ex.ToString(), null
            ), CancellationToken.None);
            throw;
        }
    }

    private async Task<(int prepared, int succeeded, int failed)> HandleBatchAsync(List<JdAddress> batch, CancellationToken ct)
    {
        var result = await jd.UpsertAddressesAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => $"{f.Item.att}:{f.Status}"));
            logger.LogWarning("JD upsert failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }

        foreach (var failure in result.Failures)
        {
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "JD", failure.Item.att,
                $"JD rejected debtor address {failure.Item.att}: {failure.Message}", null,
                JsonSerializer.SerializeToElement(new { status = failure.Status, message = failure.Message, account = failure.Item.att })), ct);
        }

        logger.LogInformation("JD upsert batch success={Success} failures={Failures}", result.SuccessCount, result.Failures.Count);
        return (batch.Count, result.SuccessCount, result.Failures.Count);
    }
}

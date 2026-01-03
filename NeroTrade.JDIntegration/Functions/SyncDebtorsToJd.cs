using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncDebtorsToJd(
    IUnicontaService uniconta,
    DebtorMapper mapper,
    IJdLogisticsService jd,
    ILogger<SyncDebtorsToJd> logger)
{
    [Function("SyncDebtorsToJd")]
    public async Task RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", Route = "sync-debtors-to-jd")] HttpRequestData req)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncDebtorsToJd started");

        var jdBatch = new List<JdAddress>(capacity: 200);
        int total = 0;
        await foreach (var debtor in uniconta.ReadDebtorsBatchedAsync(200, cts.Token))
        {
            jdBatch.Add(mapper.Map(debtor));
            if (jdBatch.Count >= 200)
            {
                await HandleBatchAsync(jdBatch, cts.Token);
                total += jdBatch.Count; 
                jdBatch.Clear();
            }
        }
        if (jdBatch.Count > 0)
        {
            await HandleBatchAsync(jdBatch, cts.Token);
            total += jdBatch.Count;
            jdBatch.Clear();
        }

        logger.LogInformation("SyncDebtorsToJd completed. TotalPrepared={Total}", total);
    }

    private async Task HandleBatchAsync(List<JdAddress> batch, CancellationToken ct)
    {
        var result = await jd.UpsertAddressesAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => $"{f.Item.att}:{f.Status}"));
            logger.LogWarning("JD upsert failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }
        logger.LogInformation("JD upsert batch success={Success} failures={Failures}", result.SuccessCount, result.Failures.Count);
    }
}
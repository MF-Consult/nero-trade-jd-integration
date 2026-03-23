using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

public sealed class SyncItemsToJd(
    IUnicontaService uniconta,
    ItemMapper mapper,
    IJdLogisticsService jd,
    ILogger<SyncItemsToJd> logger)
{
    [Function("SyncItemsToJd")]
    public async Task RunAsync([TimerTrigger("0 */2 * * * *")] TimerInfo timer)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncItemsToJd started");

        var batch = new List<JdCatalogItem>(capacity: 200);
        int total = 0;
        await foreach (var item in uniconta.ReadItemsBatchedAsync(200, cts.Token))
        {
            var jdItem = mapper.Map(item);
            if (string.IsNullOrWhiteSpace(jdItem.sku)) continue;
            batch.Add(jdItem);
            if (batch.Count >= 200)
            {
                await HandleBatchAsync(batch, cts.Token);
                total += batch.Count;
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await HandleBatchAsync(batch, cts.Token);
            total += batch.Count;
            batch.Clear();
        }

        logger.LogInformation("SyncItemsToJd completed. TotalPrepared={Total}", total);
    }

    private async Task HandleBatchAsync(List<JdCatalogItem> batch, CancellationToken ct)
    {
        var result = await jd.UpsertItemsAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => $"{f.Item.sku}:{f.Status}"));
            logger.LogWarning("JD item upsert failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }
        logger.LogInformation("JD item upsert batch success={Success} failures={Failures}", result.SuccessCount, result.Failures.Count);
    }
}
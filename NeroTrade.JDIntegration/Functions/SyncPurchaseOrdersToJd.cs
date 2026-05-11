using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class SyncPurchaseOrdersToJd(
    IUnicontaService uniconta,
    PurchaseOrderMapper mapper,
    IJdLogisticsService jd,
    ILogger<SyncPurchaseOrdersToJd> logger)
{
    [Function("SyncPurchaseOrdersToJd")]
    public async Task RunAsync([TimerTrigger("*/40 * * * * *")] TimerInfo timer)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncPurchaseOrdersToJd started");

        var batch = new List<JdIncomingShipmentCreate>(capacity: 200);
        int total = 0;
        await foreach (var po in uniconta.ReadPurchaseOrdersBatchedAsync(200, cts.Token))
        {
            var payload = mapper.Map(po);
            payload.text = $"PO {po.PurchaseNumber}";
            batch.Add(payload);
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

        logger.LogInformation("SyncPurchaseOrdersToJd completed. Total={Total}", total);
    }

    private async Task HandleBatchAsync(List<JdIncomingShipmentCreate> batch, CancellationToken ct)
    {
        var result = await jd.CreateIncomingShipmentsAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => f.Item.text));
            logger.LogWarning("Incoming shipments create failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }

        // Mark successful orders as created in Uniconta
        int markedCount = 0;
        foreach (var item in result.CreatedItems)
        {
            if (item.SourcePurchaseNumber.HasValue)
            {
                var success = await uniconta.SetPurchaseOrderHeaderFieldAsync(item.SourcePurchaseNumber.Value, UnicontaUserFields.CreatedAtJd, true, ct);
                if (success) markedCount++;
                else logger.LogError("Failed to mark PO {Po} as CreatedAtJD in Uniconta", item.SourcePurchaseNumber.Value);
            }
        }

        logger.LogInformation("JD incoming shipments create batch success={Success} marked_uniconta={Marked} failures={Failures}", 
            result.SuccessCount, markedCount, result.Failures.Count);
    }
}



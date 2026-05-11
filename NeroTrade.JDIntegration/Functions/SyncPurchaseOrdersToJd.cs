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
            if (ok) markedCreated++;
            else logger.LogError("Failed to set PO {Po} status to Oprettet in Uniconta", item.SourcePurchaseNumber.Value);
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
        }

        logger.LogInformation("JD incoming shipments batch: created_or_exists={Created} marked_oprettet={MarkedCreated} failures={Failures} marked_manuel={MarkedManual}",
            result.SuccessCount, markedCreated, result.Failures.Count, markedManual);
    }
}



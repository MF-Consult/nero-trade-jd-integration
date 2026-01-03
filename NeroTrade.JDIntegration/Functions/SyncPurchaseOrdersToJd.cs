using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public sealed class SyncPurchaseOrdersToJd
{
    private readonly IUnicontaService _uniconta;
    private readonly PurchaseOrderMapper _mapper;
    private readonly IJdLogisticsService _jd;
    private readonly ILogger<SyncPurchaseOrdersToJd> _logger;

    public SyncPurchaseOrdersToJd(IUnicontaService uniconta, PurchaseOrderMapper mapper, IJdLogisticsService jd, ILogger<SyncPurchaseOrdersToJd> logger)
    {
        _uniconta = uniconta;
        _mapper = mapper;
        _jd = jd;
        _logger = logger;
    }

    [Function("SyncPurchaseOrdersToJd")]
    public async Task RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", Route = "sync-purchaseorders-to-jd")] HttpRequestData req)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        _logger.LogInformation("SyncPurchaseOrdersToJd started");

        var batch = new List<JdIncomingShipmentCreate>(capacity: 200);
        int total = 0;
        await foreach (var po in _uniconta.ReadPurchaseOrdersBatchedAsync(200, cts.Token))
        {
            var payload = _mapper.Map(po);
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

        _logger.LogInformation("SyncPurchaseOrdersToJd completed. Total={Total}", total);
    }

    private async Task HandleBatchAsync(List<JdIncomingShipmentCreate> batch, CancellationToken ct)
    {
        var result = await _jd.UpsertIncomingShipmentsAsync(batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => f.Item.text));
            _logger.LogWarning("Incoming shipments upsert failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }
        _logger.LogInformation("JD incoming shipments upsert batch success={Success} failures={Failures}", result.SuccessCount, result.Failures.Count);
    }
}



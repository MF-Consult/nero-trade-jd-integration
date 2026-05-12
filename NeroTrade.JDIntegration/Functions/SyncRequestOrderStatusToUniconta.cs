using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Functions;

public class SyncRequestOrderStatusToUniconta
{
    private readonly IJdLogisticsService _jd;
    private readonly IUnicontaService _uniconta;
    private readonly StatusMappingConfig _config;
    private readonly IIntegrationLogger _integrationLogger;
    private readonly SupabaseOptions _supabaseOptions;
    private readonly ILogger<SyncRequestOrderStatusToUniconta> _logger;

    public SyncRequestOrderStatusToUniconta(
        IJdLogisticsService jd,
        IUnicontaService uniconta,
        IOptions<StatusMappingConfig> config,
        IIntegrationLogger integrationLogger,
        SupabaseOptions supabaseOptions,
        ILogger<SyncRequestOrderStatusToUniconta> logger)
    {
        _jd = jd;
        _uniconta = uniconta;
        _config = config.Value;
        _integrationLogger = integrationLogger;
        _supabaseOptions = supabaseOptions;
        _logger = logger;
    }

    [Function("SyncRequestOrderStatusToUniconta")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        _logger.LogInformation("SyncRequestOrderStatusToUniconta started");

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
            // 1. Get JD Inventory ID (assuming first one)
            var inventories = await _jd.GetInventoriesAsync(token);
            var inventory = inventories.FirstOrDefault();
            if (inventory == null || inventory.id == null)
            {
                _logger.LogWarning("No inventories available in JD. Aborting sync.");
                return;
            }

            // 2. Fetch all JD Request Orders
            // We fetch all because we don't know which ones changed status relative to Uniconta without checking.
            // In a larger system, we might filter by modified date if we tracked last sync time.
            _logger.LogInformation("Fetching Request Orders from JD inventory {InventoryId}...", inventory.id);
            var jdOrders = await _jd.GetRequestOrdersAsync(inventory.id.Value, token);
            _logger.LogInformation("Fetched {Count} orders from JD", jdOrders.Count);

            // 3. Fetch Uniconta orders with their current groups
            // We load them into a dictionary for fast lookup by OrderNumber
            _logger.LogInformation("Fetching Sales Orders from Uniconta...");
            var unicontaOrders = new Dictionary<int, string>();
            await foreach (var uOrder in _uniconta.ReadSalesOrdersWithGroupAsync(token))
            {
                unicontaOrders[uOrder.OrderNumber] = uOrder.Group ?? string.Empty;
            }
            _logger.LogInformation("Fetched {Count} orders from Uniconta", unicontaOrders.Count);

            // 4. Compare and Update
            int updatedCount = 0;
            int errorCount = 0;
            int skippedCount = 0;

            foreach (var jdOrder in jdOrders)
            {
                // Validate JD Order
                if (jdOrder.status == null) continue;

                int orderNumber = JdOrderHelper.GetOrderNumber(jdOrder.shopOrderId, jdOrder.text);

                if (orderNumber == 0)
                {
                    // Could not parse order number
                    continue;
                }

                // Check if we have this order in Uniconta
                if (!unicontaOrders.TryGetValue(orderNumber, out var currentGroup))
                {
                    // Order exists in JD but not found in Uniconta (or wasn't fetched)
                    continue;
                }

                // Determine target group: Stage has priority over Status
                string? targetGroup = null;
                string mappingSource = "Status";

                /*if (jdOrder.stage.HasValue && _config.JdStageToUnicontaGroup.TryGetValue(jdOrder.stage.Value, out var stageGroup))
                {
                    targetGroup = stageGroup;
                    mappingSource = "Stage";
                }
                else */
                if (jdOrder.status.HasValue && _config.JdStatusToUnicontaGroup.TryGetValue(jdOrder.status.Value, out var statusGroup))
                {
                    targetGroup = statusGroup;
                }

                if (targetGroup == null)
                {
                    // No mapping found for this status/stage, skip
                    continue;
                }

                // Check if update is needed
                if (string.Equals(currentGroup, targetGroup, StringComparison.OrdinalIgnoreCase))
                {
                    skippedCount++;
                    continue;
                }

                // Perform Update
                _logger.LogInformation("Updating Order {OrderNumber}: {Source} (Status:{JdStatus}, Stage:{JdStage}) -> Group '{TargetGroup}' (was '{CurrentGroup}')",
                    orderNumber, mappingSource, jdOrder.status, jdOrder.stage, targetGroup, currentGroup);

                var success = await _uniconta.UpdateSalesOrderGroupAsync(orderNumber, targetGroup, token);
                if (success)
                {
                    updatedCount++;
                    await _integrationLogger.LogAsync(new IntegrationLogEntry(
                        _supabaseOptions.IntegrationName, "info", "Integration", orderNumber.ToString(),
                        $"Sales order {orderNumber} status updated to '{targetGroup}' from JD.", null, null), token);
                }
                else
                {
                    errorCount++;
                    await _integrationLogger.LogAsync(new IntegrationLogEntry(
                        _supabaseOptions.IntegrationName, "error", "Uniconta", orderNumber.ToString(),
                        $"Failed to update sales order {orderNumber} status to '{targetGroup}' in Uniconta.", null, null), token);
                }
            }

            _logger.LogInformation("Sync Completed. Updated: {Updated}, Errors: {Errors}, Skipped/NoChange: {Skipped}",
                updatedCount, errorCount, skippedCount);

            if (updatedCount > 0 || errorCount > 0)
            {
                await _integrationLogger.LogAsync(new IntegrationLogEntry(
                    _supabaseOptions.IntegrationName, "info", "Integration", null,
                    $"SyncRequestOrderStatusToUniconta completed: {updatedCount} updated, {errorCount} errors, {skippedCount} unchanged.",
                    null,
                    JsonSerializer.SerializeToElement(new { updated = updatedCount, errors = errorCount, skipped = skippedCount })
                ), token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Request Order Status Sync");
            await _integrationLogger.LogAsync(new IntegrationLogEntry(
                _supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncRequestOrderStatusToUniconta run failed: {ex.Message}", ex.ToString(), null
            ), CancellationToken.None);
            throw;
        }
    }
}

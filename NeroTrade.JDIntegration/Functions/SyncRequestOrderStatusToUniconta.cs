using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Functions;

public class SyncRequestOrderStatusToUniconta(
    IJdLogisticsService jd,
    IUnicontaService uniconta,
    IOptions<StatusMappingConfig> config,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<SyncRequestOrderStatusToUniconta> logger)
{
    private readonly StatusMappingConfig _config = config.Value;

    [Function("SyncRequestOrderStatusToUniconta")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncRequestOrderStatusToUniconta started");

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
            // 1. Get JD Inventory ID (assuming first one)
            var inventories = await jd.GetInventoriesAsync(token);
            var inventory = inventories.FirstOrDefault();
            if (inventory == null || inventory.id == null)
            {
                logger.LogWarning("No inventories available in JD. Aborting sync.");
                return;
            }

            // 2. Fetch all JD Request Orders
            // We fetch all because we don't know which ones changed status relative to Uniconta without checking.
            // In a larger system, we might filter by modified date if we tracked last sync time.
            logger.LogInformation("Fetching Request Orders from JD inventory {InventoryId}...", inventory.id);
            var jdOrders = await jd.GetRequestOrdersAsync(inventory.id.Value, token);
            logger.LogInformation("Fetched {Count} orders from JD", jdOrders.Count);

            // 3. Fetch Uniconta orders with their current groups
            // We load them into a dictionary for fast lookup by OrderNumber
            logger.LogInformation("Fetching Sales Orders from Uniconta...");
            var unicontaOrders = new Dictionary<int, string>();
            await foreach (var uOrder in uniconta.ReadSalesOrdersWithGroupAsync(token))
            {
                unicontaOrders[uOrder.OrderNumber] = uOrder.Group ?? string.Empty;
            }
            logger.LogInformation("Fetched {Count} orders from Uniconta", unicontaOrders.Count);

            // 4. Compare and Update
            int updatedCount = 0;
            int errorCount = 0;
            int skippedCount = 0;

            foreach (var jdOrder in jdOrders)
            {
                // Validate JD Order
                if (jdOrder.status == null) continue;

                int orderNumber = JdOrderHelper.GetOrderNumber(jdOrder.shopOrderId, jdOrder.text, jdOrder.deliveryNoteText);

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

                // Stage takes priority over Status once JD has actually progressed the order past
                // Pending(0). Reason: JD's status only flips Pending→Approved→Denied/Cancelled and
                // then sticks; further progression (Planned→PendingDispatch→Dispatched→Closed) only
                // shows up on stage. Without this branch, Group froze at "Godkendt" after approval.
                // Guard `stage > Pending`: at stage=0 both status and stage map to "Afventer", but a
                // freshly Approved order has status=Approved/stage=Pending — letting status win there
                // keeps "Godkendt" instead of regressing to "Afventer".
                string? targetGroup = null;
                string mappingSource = "Status";

                if (jdOrder.stage.HasValue
                    && jdOrder.stage.Value > JdRequestOrderStage.Pending
                    && _config.JdStageToUnicontaGroup.TryGetValue(jdOrder.stage.Value, out var stageGroup))
                {
                    targetGroup = stageGroup;
                    mappingSource = "Stage";
                }
                else if (jdOrder.status.HasValue && _config.JdStatusToUnicontaGroup.TryGetValue(jdOrder.status.Value, out var statusGroup))
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
                logger.LogInformation("Updating Order {OrderNumber}: {Source} (Status:{JdStatus}, Stage:{JdStage}) -> Group '{TargetGroup}' (was '{CurrentGroup}')",
                    orderNumber, mappingSource, jdOrder.status, jdOrder.stage, targetGroup, currentGroup);

                var success = await uniconta.UpdateSalesOrderGroupAsync(orderNumber, targetGroup, token);
                if (success)
                {
                    updatedCount++;
                    await integrationLogger.LogAsync(new IntegrationLogEntry(
                        supabaseOptions.IntegrationName, "info", "Integration", orderNumber.ToString(),
                        $"Sales order {orderNumber} status updated to '{targetGroup}' from JD.", null, null), token);
                }
                else
                {
                    errorCount++;
                    await integrationLogger.LogAsync(new IntegrationLogEntry(
                        supabaseOptions.IntegrationName, "error", "Uniconta", orderNumber.ToString(),
                        $"Failed to update sales order {orderNumber} status to '{targetGroup}' in Uniconta.", null, null), token);
                }
            }

            logger.LogInformation("Sync Completed. Updated: {Updated}, Errors: {Errors}, Skipped/NoChange: {Skipped}",
                updatedCount, errorCount, skippedCount);

            if (updatedCount > 0 || errorCount > 0)
            {
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "info", "Integration", null,
                    $"SyncRequestOrderStatusToUniconta completed: {updatedCount} updated, {errorCount} errors, {skippedCount} unchanged.",
                    null,
                    JsonSerializer.SerializeToElement(new { updated = updatedCount, errors = errorCount, skipped = skippedCount })
                ), token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during Request Order Status Sync");
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncRequestOrderStatusToUniconta run failed: {ex.Message}", ex.ToString(), null
            ), CancellationToken.None);
            throw;
        }
    }
}

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

public sealed class SyncRequestOrderStatusToUniconta(
    IJdLogisticsService jd,
    IUnicontaService uniconta,
    IOptions<StatusMappingConfig> config,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<SyncRequestOrderStatusToUniconta> logger)
{
    private readonly StatusMappingConfig _config = config.Value;

    [Function("SyncRequestOrderStatusToUniconta")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var logScope = new IntegrationLogScope();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = logScope.CorrelationId });
        logger.LogInformation("SyncRequestOrderStatusToUniconta started");

        var token = cancellationToken;

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

            // JD lets multiple request orders point back to the same Uniconta SO (via
            // deliveryNoteText "SO {n}"). When duplicates exist, iterating naively makes
            // Group flap each tick depending on JD's response order — see SO 2135 in prod.
            // Group by resolved SO number and pick one winner deterministically.
            var jdOrdersBySo = jdOrders
                .Where(o => o.status.HasValue)
                .Select(o => (orderNumber: JdOrderHelper.GetOrderNumber(o.shopOrderId, o.text, o.deliveryNoteText), jdOrder: o))
                .Where(p => p.orderNumber != 0)
                .GroupBy(p => p.orderNumber);

            foreach (var soGroup in jdOrdersBySo)
            {
                int orderNumber = soGroup.Key;

                // Check if we have this order in Uniconta
                if (!unicontaOrders.TryGetValue(orderNumber, out var currentGroup))
                {
                    // Order exists in JD but not found in Uniconta (or wasn't fetched)
                    continue;
                }

                var candidates = soGroup.Select(p => p.jdOrder).ToList();
                var jdOrder = PickWinner(candidates);

                if (candidates.Count > 1)
                {
                    var ids = string.Join(",", candidates.Select(c => c.id?.ToString() ?? "?"));
                    logger.LogInformation(
                        "SO {OrderNumber}: {Count} JD orders matched (ids: {Ids}) — using id={WinnerId} (status={Status}, stage={Stage})",
                        orderNumber, candidates.Count, ids, jdOrder.id, jdOrder.status, jdOrder.stage);
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
                        $"Sales order {orderNumber} status updated to '{targetGroup}' from JD.", null, null)
                    {
                        CorrelationId = logScope.CorrelationId
                    }, token);
                }
                else
                {
                    errorCount++;
                    await integrationLogger.LogAsync(new IntegrationLogEntry(
                        supabaseOptions.IntegrationName, "error", "Uniconta", orderNumber.ToString(),
                        $"Failed to update sales order {orderNumber} status to '{targetGroup}' in Uniconta.", null,
                        JsonSerializer.SerializeToElement(new { orderNumber, targetGroup, currentGroup, jdStatus = jdOrder.status, jdStage = jdOrder.stage }))
                    {
                        CorrelationId = logScope.CorrelationId,
                        ErrorCode = "UNICONTA_CRUD_FAILED",
                        Retryable = true,
                        SuggestedAction = "Auto-recovers on next 5-min tick; if persistent, call /admin/override-order-status."
                    }, token);
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
                    JsonSerializer.SerializeToElement(new { updated = updatedCount, errors = errorCount, skipped = skippedCount }))
                {
                    CorrelationId = logScope.CorrelationId
                }, token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during Request Order Status Sync");
            var classified = ErrorCodeClassifier.Classify(ex);
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncRequestOrderStatusToUniconta run failed: {LogSanitizer.Describe(ex)}", null, null)
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = classified.ErrorCode,
                Retryable = classified.Retryable,
                SuggestedAction = classified.SuggestedAction
            }, CancellationToken.None);
            throw;
        }
    }

    // Terminal = JD has effectively closed the order with no progression intent. A terminal
    // order must never overwrite a live sibling's status — see SO 2135 where a Cancelled stub
    // was racing a Dispatched live order.
    private static bool IsTerminal(JdRequestOrder o) =>
        o.status == 2 || o.status == 3 || o.stage == JdRequestOrderStage.Denied;

    private static JdRequestOrder PickWinner(IReadOnlyList<JdRequestOrder> candidates)
    {
        if (candidates.Count == 1) return candidates[0];

        // Prefer non-terminal candidates; only fall back to terminal pool when ALL are dead,
        // so Uniconta still lands on a stable terminal status instead of flapping.
        var live = candidates.Where(o => !IsTerminal(o)).ToList();
        var pool = live.Count > 0 ? live : candidates;

        return pool
            .OrderByDescending(o => o.stage ?? -1)
            .ThenByDescending(o => o.createdOn ?? DateTime.MinValue)
            .First();
    }
}

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Functions;

public class SyncReceivedQuantityToUniconta(
    IJdLogisticsService jd,
    IUnicontaService uniconta,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<SyncReceivedQuantityToUniconta> logger)
{
    [Function("SyncReceivedQuantityToUniconta")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncReceivedQuantityToUniconta started");

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
            // 1. Fetch all Incoming Shipments from JD
            logger.LogInformation("Fetching Incoming Shipments from JD...");
            var allShipments = await jd.GetIncomingShipmentsAsync(token);

            // Filter for completed shipments (Status = 1) and modified within last 24h.
            // The function runs every 5 minutes; a 24h window gives ample overlap so nothing is missed.
            var cutoffTime = DateTime.UtcNow.AddDays(-1);
            var relevantShipments = allShipments
                .Where(s => s.status == 1 && s.modifiedOn >= cutoffTime)
                .ToList();

            logger.LogInformation("Fetched {Total} shipments. Found {Relevant} completed (status=1) and modified since {Cutoff}",
                allShipments.Count, relevantShipments.Count, cutoffTime);

            int updatedCount = 0;
            int errorCount = 0;
            int skippedCount = 0;

            foreach (var summary in relevantShipments)
            {
                if (!summary.id.HasValue) continue;

                // Parse PO number from text field "PO {number}"
                int purchaseNumber = JdOrderHelper.GetPurchaseOrderNumber(summary.text);

                if (purchaseNumber == 0)
                {
                    logger.LogWarning("Could not parse Purchase Order number from shipment text: '{Text}' (ID: {Id})", summary.text, summary.id);
                    skippedCount++;
                    continue;
                }

                // Mark the Purchase Order as fully handled in JD.
                // We do this regardless of line items, as long as we have identified the PO.
                await uniconta.SetPurchaseOrderHeaderFieldAsync(purchaseNumber, UnicontaUserFields.PurchaseOrderJdStatus, PurchaseOrderJdStatusValues.Completed, token);

                // Fetch full details to get registered items
                var shipment = await jd.GetIncomingShipmentByIdAsync(summary.id.Value, token);
                if (shipment == null)
                {
                    logger.LogWarning("Could not fetch details for shipment {Id}", summary.id);
                    errorCount++;
                    await integrationLogger.LogAsync(new IntegrationLogEntry(
                        supabaseOptions.IntegrationName, "error", "JD", purchaseNumber.ToString(),
                        $"Could not fetch incoming shipment {summary.id} details from JD (PO {purchaseNumber}).", null, null), token);
                    continue;
                }

                if (shipment.registeredItems == null || shipment.registeredItems.Count == 0)
                {
                    logger.LogWarning("Shipment {Id} (PO {Po}) has no registered items", shipment.id, purchaseNumber);
                    skippedCount++;
                    continue;
                }

                var quantitiesBySku = JdOrderHelper.CalculateReceivedQuantitiesBySku(shipment.registeredItems);

                logger.LogInformation("Processing PO {Po} (Shipment {Id}) with {Count} registered SKUs", purchaseNumber, shipment.id, quantitiesBySku.Count);

                bool poUpdated = false;

                foreach (var (sku, receivedQty) in quantitiesBySku)
                {
                    // Update Uniconta
                    var success = await uniconta.UpdatePurchaseOrderLineQuantityAsync(purchaseNumber, sku, receivedQty, token);

                    if (!success)
                    {
                        logger.LogError("Failed to sync line {Sku} for PO {Po}", sku, purchaseNumber);
                        errorCount++;
                        await integrationLogger.LogAsync(new IntegrationLogEntry(
                            supabaseOptions.IntegrationName, "error", "Uniconta", purchaseNumber.ToString(),
                            $"Failed to update received quantity for SKU {sku} on PO {purchaseNumber}.", null,
                            JsonSerializer.SerializeToElement(new { sku, purchaseNumber })), token);
                    }
                    else
                    {
                        poUpdated = true;
                    }
                }

                if (poUpdated)
                {
                    updatedCount++;
                    await integrationLogger.LogAsync(new IntegrationLogEntry(
                        supabaseOptions.IntegrationName, "info", "Integration", purchaseNumber.ToString(),
                        $"Received quantities for PO {purchaseNumber} registered from JD.", null, null), token);
                }
            }

            logger.LogInformation("Sync Completed. Processed POs: {Updated}, Line Errors: {Errors}, Skipped Shipments: {Skipped}",
                updatedCount, errorCount, skippedCount);

            if (relevantShipments.Count > 0)
            {
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "info", "Integration", null,
                    $"SyncReceivedQuantityToUniconta completed: {updatedCount} POs updated, {errorCount} line errors, {skippedCount} shipments skipped.",
                    null,
                    JsonSerializer.SerializeToElement(new { processedPOs = updatedCount, lineErrors = errorCount, skippedShipments = skippedCount })
                ), token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during Received Quantity Sync");
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncReceivedQuantityToUniconta run failed: {ex.Message}", ex.ToString(), null
            ), CancellationToken.None);
            throw;
        }
    }
}

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.Scheduling;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncReceivedQuantityToUniconta(
    IJdLogisticsService jd,
    IUnicontaService uniconta,
    SyncScheduler scheduler,
    IIntegrationLogger integrationLogger,
    ILogger<SyncReceivedQuantityToUniconta> logger)
{
    // Invoked by SyncDispatcher, not by its own timer — see that class. The scheduler gate below
    // still decides whether this tick does any work, so the configured cadence is unchanged.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!scheduler.TryBeginRun("ReceivedQuantity", DateTime.UtcNow)) return;

        await using var run = integrationLogger.BeginRun("SyncReceivedQuantityToUniconta");
        var logScope = run.Scope;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = logScope.CorrelationId });
        logger.LogInformation("SyncReceivedQuantityToUniconta started");

        var token = cancellationToken;

        try
        {
            // 1. Fetch all Incoming Shipments from JD
            logger.LogInformation("Fetching Incoming Shipments from JD...");
            var allShipments = await jd.GetIncomingShipmentsAsync(token);

            // Filter for completed shipments (Status = 1) and modified within last 24h.
            // The function runs every 15 minutes; a 24h window gives ample overlap so nothing is missed.
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
                var statusWrite = await uniconta.SetPurchaseOrderHeaderFieldAsync(purchaseNumber, UnicontaUserFields.PurchaseOrderJdStatus, PurchaseOrderJdStatusValues.Completed, token);

                if (statusWrite == UnicontaWriteResult.OrderNotFound)
                {
                    // The order is booked: it has left the open-order table, and its goods receipt is
                    // already recorded in Uniconta by the booking itself. There is nothing to write a
                    // received quantity to — posted invoice lines are immutable — so we set the status on
                    // the invoice header (where the booked order's user fields live) and move on.
                    //
                    // Before this, the sync kept trying and logged one UNICONTA_CRUD_FAILED per SKU per
                    // tick for the 24 hours the JD shipment stayed in the lookback window — 85 rows for
                    // PO 34, 49 for PO 39, ~85 for PO 10 — each one waking the Hermes agent. Those were
                    // never failures; they were the normal state of a booked order.
                    await uniconta.SetPurchaseInvoiceHeaderFieldsAsync(purchaseNumber, new Dictionary<string, object>
                    {
                        [UnicontaUserFields.PurchaseOrderJdStatus] = PurchaseOrderJdStatusValues.Completed,
                    }, token);

                    logger.LogInformation(
                        "PO {Po} is booked; received quantity is already in Uniconta's books. Set JD status on the posted invoice instead.",
                        purchaseNumber);
                    skippedCount++;
                    continue;
                }

                // Fetch full details to get registered items
                var shipment = await jd.GetIncomingShipmentByIdAsync(summary.id.Value, token);
                if (shipment == null)
                {
                    logger.LogWarning("Could not fetch details for shipment {Id}", summary.id);
                    errorCount++;
                    await integrationLogger.LogAsync(new IntegrationLogEntry(
                        integrationLogger.IntegrationName, "error", "JD", purchaseNumber.ToString(),
                        $"Could not fetch incoming shipment {summary.id} details from JD (PO {purchaseNumber}).", null,
                        JsonSerializer.SerializeToElement(new { shipmentId = summary.id, purchaseNumber }))
                    {
                        CorrelationId = logScope.CorrelationId,
                        ErrorCode = "JD_LOOKUP_MISS",
                        Retryable = true,
                        SuggestedAction = "Auto-recovers on next tick (15 min); if persistent, the shipment may have been deleted in JD."
                    }, token);
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
                    var lineWrite = await uniconta.UpdatePurchaseOrderLineQuantityAsync(purchaseNumber, sku, receivedQty, token);

                    if (lineWrite == UnicontaWriteResult.OrderNotFound)
                    {
                        // Booked between the status write above and now. Same reasoning as there: nothing
                        // to write, and not a failure. Stop on this shipment rather than repeat it per SKU.
                        logger.LogInformation("PO {Po} was booked mid-run; leaving the remaining lines to the books.", purchaseNumber);
                        break;
                    }

                    if (lineWrite == UnicontaWriteResult.Failed)
                    {
                        logger.LogError("Failed to sync line {Sku} for PO {Po}", sku, purchaseNumber);
                        errorCount++;
                        await integrationLogger.LogAsync(new IntegrationLogEntry(
                            integrationLogger.IntegrationName, "error", "Uniconta", purchaseNumber.ToString(),
                            $"Failed to update received quantity for SKU {sku} on PO {purchaseNumber}.", null,
                            JsonSerializer.SerializeToElement(new { sku, purchaseNumber, receivedQty }))
                        {
                            CorrelationId = logScope.CorrelationId,
                            ErrorCode = "UNICONTA_CRUD_FAILED",
                            Retryable = true,
                            SuggestedAction = "The order exists but the write was rejected, or JD registered a SKU that is not on the order. Check the PO line for that SKU in Uniconta."
                        }, token);
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
                        integrationLogger.IntegrationName, "info", "Integration", purchaseNumber.ToString(),
                        $"Received quantities for PO {purchaseNumber} registered from JD.", null, null)
                    {
                        CorrelationId = logScope.CorrelationId
                    }, token);
                    await integrationLogger.MarkResolvedAsync(
                        integrationLogger.IntegrationName,
                        purchaseNumber.ToString(),
                        logScope.CorrelationId,
                        token);
                }
            }

            logger.LogInformation("Sync Completed. Processed POs: {Updated}, Line Errors: {Errors}, Skipped Shipments: {Skipped}",
                updatedCount, errorCount, skippedCount);

            if (relevantShipments.Count > 0)
            {
                run.AttachCompletionPayload(new { processed = updatedCount + errorCount + skippedCount, succeeded = updatedCount, failed = errorCount, skipped = skippedCount });
            }
        }
        catch (UnicontaConnectionUnavailableException ex)
        {
            // Uniconta was unreachable this tick — nothing actionable on our side, and the next
            // tick reconnects. Recorded as a warning run (see ErrorCodeClassifier) and deliberately
            // NOT rethrown, so it does not also surface as a failed invocation in App Insights.
            run.MarkFailed(ex);
            run.ExitReason = "uniconta_unavailable";
            logger.LogWarning(ex, "Uniconta unavailable; skipping this SyncReceivedQuantityToUniconta tick");
        }
        catch (Exception ex)
        {
            run.MarkFailed(ex);
            logger.LogError(ex, "Error during Received Quantity Sync");
            throw;
        }
    }
}

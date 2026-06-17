namespace NeroTrade.JDIntegration.Services.ExternalIntegration;

using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using Repositories;

public sealed class JdLogisticsService(
    IJdRepository repository,
    JdReadCache cache,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<JdLogisticsService> logger)
    : IJdLogisticsService
{
    private readonly ILogger<JdLogisticsService> _logger = logger;
    private IDictionary<string, JdAddress>? _existingByAtt;
    private IDictionary<string, JdCatalogItem>? _existingItemsBySku;
    private IReadOnlyList<JdContainerType>? _containerTypes;

    /// <summary>
    /// Wraps a cached JD lookup so a refresh failure becomes visible in <c>integration_logs</c>
    /// (Hermes/dashboards can then see "JD was down between X and Y") and the exception still
    /// propagates so <see cref="JdReadCache"/> can serve stale or short-circuit accordingly.
    /// Fires once per refresh attempt — never per tick — because the cache only invokes the loader
    /// on TTL expiry / backoff expiry, not on every cache hit.
    /// </summary>
    private async Task<TResult> LoadWithFailureLoggingAsync<TResult>(
        string endpoint,
        Func<Task<TResult>> load,
        CancellationToken cancellationToken)
    {
        try
        {
            return await load();
        }
        catch (JdLookupFailedException ex)
        {
            // CorrelationId is fresh per refresh — the loader runs inside the cache and may be
            // serving N concurrent function invocations; there is no single "invocation" to tie to.
            // The error_code is what Hermes/dashboards filter on.
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName,
                "warning",
                "JD",
                null,
                $"JD GET {endpoint} failed (status {ex.StatusCode}). Cache will serve the last successful value if available.",
                null,
                null)
            {
                CorrelationId = Guid.NewGuid(),
                ErrorCode = "JD_LOOKUP_FAILED",
                Retryable = true,
                SuggestedAction = "Transient JD-side failure; cache will retry on its own backoff. Investigate if it persists across multiple ticks."
            }, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Upsert addresses to JD.
    /// </summary>
    /// <param name="addresses">Addresses to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upsert result.</returns>
    public async Task<UpsertResult<JdAddress>> UpsertAddressesAsync(IEnumerable<JdAddress> addresses, CancellationToken cancellationToken)
    {
        _existingByAtt = await cache.GetAddressesByAttAsync(
            () => LoadWithFailureLoggingAsync("addresses", () => repository.GetAddressesAsync(cancellationToken), cancellationToken),
            cancellationToken);

        var result = new UpsertResult<JdAddress>();
        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address.att))
            {
                result.Failures.Add(new UpsertFailure<JdAddress>(address, 0, "Missing att (external id)"));
                continue;
            }

            if (_existingByAtt.TryGetValue(address.att, out var existing) && existing.id.HasValue)
            {
                var updated = await repository.UpdateAddressAsync(existing.id.Value, address, cancellationToken);
                if (updated.ok) result.SuccessCount++; else result.Failures.Add(new UpsertFailure<JdAddress>(address, updated.status, updated.message));
            }
            else
            {
                var created = await repository.CreateAddressAsync(address, cancellationToken);
                if (created.ok)
                {
                    result.SuccessCount++;
                    result.CreatedItems.Add(address);
                    if (created.returned?.att != null) _existingByAtt[created.returned.att] = created.returned;
                }
                else
                {
                    result.Failures.Add(new UpsertFailure<JdAddress>(address, created.status, created.message));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Upsert items to JD.
    /// </summary>
    /// <param name="items">Items to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upsert result.</returns>
    public async Task<UpsertResult<JdCatalogItem>> UpsertItemsAsync(IEnumerable<JdCatalogItem> items, CancellationToken cancellationToken)
    {
        _existingItemsBySku = await cache.GetItemsBySkuAsync(
            () => LoadWithFailureLoggingAsync("catalog", () => repository.GetCatalogItemsAsync(cancellationToken), cancellationToken),
            cancellationToken);

        var result = new UpsertResult<JdCatalogItem>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.sku))
            {
                result.Failures.Add(new UpsertFailure<JdCatalogItem>(item, 0, "Missing sku (external id)"));
                continue;
            }

            if (_existingItemsBySku.TryGetValue(item.sku, out var existing) && existing.id.HasValue)
            {
                item.id = existing.id.Value;
                var updated = await repository.UpdateCatalogItemAsync(existing.id.Value, item, cancellationToken);
                if (updated.ok) result.SuccessCount++; else result.Failures.Add(new UpsertFailure<JdCatalogItem>(item, updated.status, updated.message));
            }
            else
            {
                var created = await repository.CreateCatalogItemAsync(item, cancellationToken);
                if (created.ok)
                {
                    result.SuccessCount++;
                    result.CreatedItems.Add(item);
                    if (created.returned?.id != null) _existingItemsBySku[item.sku] = created.returned;
                }
                else
                {
                    result.Failures.Add(new UpsertFailure<JdCatalogItem>(item, created.status, created.message));
                }
            }
        }
        return result;
    }

    // Incoming shipments (purchase orders)
    public async Task<CreateResult<JdIncomingShipmentCreate>> CreateIncomingShipmentsAsync(IEnumerable<JdIncomingShipmentCreate> shipments, CancellationToken cancellationToken)
    {
        _existingItemsBySku = await cache.GetItemsBySkuAsync(
            () => LoadWithFailureLoggingAsync("catalog", () => repository.GetCatalogItemsAsync(cancellationToken), cancellationToken),
            cancellationToken);
        _containerTypes = await cache.GetContainerTypesAsync(
            () => LoadWithFailureLoggingAsync("containertypes", () => repository.GetContainerTypesAsync(cancellationToken), cancellationToken),
            cancellationToken);

        // JD has no external-id lookup for incoming shipments, so we dedupe on the "PO {n}" text we set.
        // Fetch all statuses (not just approved) so a shipment that exists but is still a draft is also
        // detected — that prevents both a duplicate create and lets a later run catch up the Uniconta status.
        var existingPurchaseNumbers = (await repository.GetIncomingShipmentsAsync(cancellationToken, status: null))
            .Select(s => JdOrderHelper.GetPurchaseOrderNumber(s.text))
            .Where(n => n != 0)
            .ToHashSet();

        var result = new CreateResult<JdIncomingShipmentCreate>();
        foreach (var shipment in shipments)
        {
            var key = shipment.text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                result.Failures.Add(new UpsertFailure<JdIncomingShipmentCreate>(shipment, 0, "Missing key text for incoming shipment"));
                continue;
            }

            var purchaseNumber = shipment.SourcePurchaseNumber ?? JdOrderHelper.GetPurchaseOrderNumber(shipment.text);
            if (purchaseNumber != 0 && existingPurchaseNumbers.Contains(purchaseNumber))
            {
                // Already in JD. Treat as success so the caller still flags it in Uniconta and stops re-sending it.
                _logger.LogInformation("Incoming shipment for PO {Po} already exists in JD, skipping create", purchaseNumber);
                result.SuccessCount++;
                result.CreatedItems.Add(shipment);
                continue;
            }

            // JD matches an incoming-shipment line to a catalog item ONLY via catalog.id. If we
            // cannot resolve a line's SKU to a real catalog id we must NOT send it: JD would
            // register the line as "Ukendt" and the warehouse would not know what it received.
            // Fail the whole shipment so the caller parks the PO for manual handling with the SKUs.
            var unresolvedSkus = ResolveCatalogItems(shipment);
            if (unresolvedSkus.Count > 0)
            {
                var skuList = string.Join(", ", unresolvedSkus);
                _logger.LogWarning(
                    "Incoming shipment '{Text}' has {Count} line(s) with no matching JD catalog item: {Skus}",
                    shipment.text, unresolvedSkus.Count, skuList);
                result.Failures.Add(new UpsertFailure<JdIncomingShipmentCreate>(shipment, 0,
                    $"No matching JD catalog item for SKU(s): {skuList}. The item must exist in JD's catalog (synced via SyncItemsToJd) before this purchase order can be received."));
                continue;
            }

            await SetContainerTypesAsync(shipment, cancellationToken);

            var upsert = await repository.UpsertIncomingShipmentAsync(shipment, cancellationToken);
            if (upsert.ok)
            {
                result.SuccessCount++;
                result.CreatedItems.Add(shipment);
                if (purchaseNumber != 0) existingPurchaseNumbers.Add(purchaseNumber);
            }
            else
            {
                result.Failures.Add(new UpsertFailure<JdIncomingShipmentCreate>(shipment, upsert.status, upsert.message));
            }
        }
        return result;
    }

    public Task<IReadOnlyList<JdIncomingShipment>> GetIncomingShipmentsAsync(CancellationToken cancellationToken)
        => repository.GetIncomingShipmentsAsync(cancellationToken);

    public async Task<JdIncomingShipment?> GetIncomingShipmentByIdAsync(long id, CancellationToken cancellationToken)
    {
        var result = await repository.GetIncomingShipmentByIdAsync(id, cancellationToken);
        return result.ok ? result.returned : null;
    }

    public async Task<UpsertResult<JdInventory>> UpsertInventoriesAsync(IEnumerable<JdInventory> inventories, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<JdInventory>> GetInventoriesAsync(CancellationToken cancellationToken)
        => cache.GetInventoriesAsync(
            () => LoadWithFailureLoggingAsync("inventories", () => repository.GetInventoriesAsync(cancellationToken), cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<JdRequestOrder>> GetRequestOrdersAsync(long inventoryId, CancellationToken cancellationToken)
        => repository.GetRequestOrdersAsync(inventoryId, cancellationToken);

    public Task<(bool ok, int status, string message)> DeleteRequestOrderAsync(long inventoryId, long requestOrderId, CancellationToken cancellationToken)
        => repository.DeleteRequestOrderAsync(inventoryId, requestOrderId, cancellationToken);

    public async Task<UpsertResult<JdRequestOrderCreate>> UpsertRequestOrdersAsync(long inventoryId, IEnumerable<JdRequestOrderCreate> orders, CancellationToken cancellationToken)
    {
        // Build existing map keyed by shop order identifier. The "SO {n}" reference leads the text
        // field ("Intern Note") on outgoing orders; JdOrderHelper falls back to deliveryNoteText so
        // legacy orders (key on the delivery-note text) keep matching.
        // Exclude cancelled (Annulleret) orders from the dedup map. JD does not hard-remove a cancelled
        // request order (DELETE returns 204 but it lingers in the list), so a dead cancelled order must
        // not match an incoming sales order — otherwise the re-upload is skipped, or tries to "recreate"
        // the un-removable order. Treating cancelled as absent lets a fresh order be created.
        var existing = (await repository.GetRequestOrdersAsync(inventoryId, cancellationToken))
            .Where(r => r.status != JdRequestOrderStatus.Cancelled)
            .Select(r => new { Order = r, Key = JdOrderHelper.GetOrderNumberString(r.shopOrderId, r.text, r.deliveryNoteText) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.OrdinalIgnoreCase);

        var result = new UpsertResult<JdRequestOrderCreate>();
        foreach (var order in orders)
        {
            var key = JdOrderHelper.GetOrderNumberString(order.shopOrderId, order.text, order.deliveryNoteText);
            if (string.IsNullOrWhiteSpace(key))
            {
                result.Failures.Add(new UpsertFailure<JdRequestOrderCreate>(order, 0, "Missing shop order identifier"));
                continue;
            }
            
            if (existing.TryGetValue(key, out var existingOrder))
            {
                // Check if order needs to be updated (deleted and recreated)
                if (existingOrder.id.HasValue && RequiresRecreation(existingOrder, order))
                {
                    // JD swagger on DELETE /api/inventories/{inventoryid}/requestorders/{id}:
                    // "If the RequestOrder is approved and pending dispatch it wont be possible."
                    // Skip recreation rather than fire-and-fail-on-delete, and surface a clear reason.
                    if (JdRequestOrderStage.IsPastDeletionThreshold(existingOrder.stage))
                    {
                        var stageLabel = JdRequestOrderStage.Describe(existingOrder.stage);
                        _logger.LogWarning(
                            "Request order {ShopOrderId} (JD id {JdOrderId}) has changed in Uniconta but is already at stage {Stage} in JD — skipping recreation",
                            key, existingOrder.id, stageLabel);
                        result.Failures.Add(new UpsertFailure<JdRequestOrderCreate>(
                            order,
                            0,
                            $"Existing JD order {existingOrder.id} is at stage {stageLabel} and can no longer be deleted/recreated. Update the order directly in JD or cancel it before re-syncing."));
                        continue;
                    }

                    _logger.LogInformation("Request order {ShopOrderId} has significant changes, deleting and recreating", key);

                    var deleted = await repository.DeleteRequestOrderAsync(inventoryId, existingOrder.id.Value, cancellationToken);
                    if (!deleted.ok)
                    {
                        result.Failures.Add(new UpsertFailure<JdRequestOrderCreate>(order, deleted.status, $"Failed to delete existing order: {deleted.message}"));
                        continue;
                    }

                    var created = await repository.CreateRequestOrderAsync(inventoryId, order, cancellationToken);
                    if (created.ok) { result.SuccessCount++; RecordJdId(result, order, created.returned?.id); }
                    else result.Failures.Add(new UpsertFailure<JdRequestOrderCreate>(order, created.status, created.message));
                }
                else
                {
                    // No significant changes, skip
                    _logger.LogDebug("Request order {ShopOrderId} already exists with no significant changes, skipping", key);
                    result.SuccessCount++;
                    RecordJdId(result, order, existingOrder.id); // backfill JD id onto already-synced orders
                }
                continue;
            }

            var createdNew = await repository.CreateRequestOrderAsync(inventoryId, order, cancellationToken);
            if (createdNew.ok) { result.SuccessCount++; RecordJdId(result, order, createdNew.returned?.id); }
            else result.Failures.Add(new UpsertFailure<JdRequestOrderCreate>(order, createdNew.status, createdNew.message));
        }
        return result;
    }

    // Map JD's request-order id back to the source (Uniconta) order number so the caller can write
    // it onto the sales order. No-op when either id is missing.
    private static void RecordJdId(UpsertResult<JdRequestOrderCreate> result, JdRequestOrderCreate order, long? jdId)
    {
        if (order.SourceOrderNumber.HasValue && jdId.HasValue)
            result.JdOrderIdBySourceOrder[order.SourceOrderNumber.Value] = jdId.Value;
    }
    
    // GetShopOrderKey method removed in favor of JdOrderHelper.GetOrderNumberString

    /// <summary>
    /// Determines if a request order needs to be deleted and recreated due to significant changes.
    /// Compares date, address, contact person, product items, and tracking notes.
    /// </summary>
    private static bool RequiresRecreation(JdRequestOrder existing, JdRequestOrderCreate incoming)
    {
        // Compare tracking note
        if (!string.Equals(existing.trackingNote, incoming.trackingNote, StringComparison.Ordinal))
        {
            return true;
        }

        // Compare delivery note text
        if (!string.Equals(existing.deliveryNoteText, incoming.deliveryNoteText, StringComparison.Ordinal))
        {
            return true;
        }

        // Compare Shipmondo carrier code
        if (!string.Equals(existing.shipmondo?.carrierCode, incoming.shipmondo?.carrierCode, StringComparison.Ordinal))
        {
            return true;
        }

        // Compare address
        if (HasAddressChanged(existing.inOutAddress, incoming.address))
        {
            return true;
        }

        // Compare contact person
        if (HasContactChanged(existing.inOutContact, incoming.contactPerson))
        {
            return true;
        }

        // Compare product items
        if (HasProductItemsChanged(existing.productItems, incoming.productItems))
        {
            return true;
        }

        return false;
    }

    private static bool HasAddressChanged(JdRequestOrderAddress? existing, JdAddress? incoming)
    {
        // If one is null and the other isn't, they're different
        if ((existing == null) != (incoming == null))
        {
            return true;
        }

        if (existing == null || incoming == null)
        {
            return false;
        }

        return !string.Equals(existing.name, incoming.name, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.street, incoming.street, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.zip, incoming.zip, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.city, incoming.city, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.countryCode, incoming.countryCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasContactChanged(JdRequestOrderContact? existing, JdRequestOrderContactPerson? incoming)
    {
        // If one is null and the other isn't, they're different
        if ((existing == null) != (incoming == null))
        {
            return true;
        }

        if (existing == null || incoming == null)
        {
            return false;
        }

        return !string.Equals(existing.name, incoming.name, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.email, incoming.email, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.telephoneDirect, incoming.telephoneDirect, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(existing.telephoneMobile, incoming.telephoneMobile, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasProductItemsChanged(List<JdRequestOrderProductItemDetail>? existing, List<JdRequestOrderProductItem> incoming)
    {
        if (existing == null)
        {
            return incoming.Count > 0;
        }

        // Different count = different
        if (existing.Count != incoming.Count)
        {
            return true;
        }

        // Compare items by SKU and quantity (order-independent comparison)
        var existingMap = existing
            .Where(e => e.catalog?.sku != null)
            .GroupBy(e => e.catalog!.sku!)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.quantity), StringComparer.OrdinalIgnoreCase);

        var incomingMap = incoming
            .Where(i => i.catalog?.sku != null)
            .GroupBy(i => i.catalog!.sku!)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.quantity), StringComparer.OrdinalIgnoreCase);

        // Check if all SKUs and quantities match
        if (existingMap.Count != incomingMap.Count)
        {
            return true;
        }

        foreach (var kvp in existingMap)
        {
            if (!incomingMap.TryGetValue(kvp.Key, out var incomingQty) || kvp.Value != incomingQty)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves each line's catalog reference (<c>catalog.id</c>) against JD's catalog by SKU —
    /// the only identifier JD uses to match a line to a catalog item. Returns the SKUs of any
    /// lines that could not be matched so the caller can fail the shipment instead of silently
    /// sending a line JD would register as "Ukendt".
    /// </summary>
    private List<string> ResolveCatalogItems(JdIncomingShipmentCreate shipment)
    {
        var unresolved = new List<string>();
        foreach (var line in shipment.lines)
        {
            // A line with no SKU is the pure container parent (the pallet/container itself, set from
            // the Lagerhotel fields in PurchaseOrderMapper). It has no catalog item — JD identifies it
            // via inventoryContainerType, set later in SetContainerTypesAsync — so skip catalog
            // resolution. Product lines without a SKU are already filtered out in the mapper, so the
            // only SKU-less line that reaches here is that container parent.
            if (string.IsNullOrWhiteSpace(line.Sku))
                continue;

            // Use Sku for lookup if available, otherwise try externalIdentification (fallback).
            // Trim to match the catalog cache, which is keyed on the trimmed SKU (see ItemMapper).
            var skuToLookup = (!string.IsNullOrWhiteSpace(line.Sku) ? line.Sku : line.externalIdentification)?.Trim();

            if (!string.IsNullOrWhiteSpace(skuToLookup)
                && _existingItemsBySku!.TryGetValue(skuToLookup, out var item)
                && item.id.HasValue)
            {
                line.catalog = new JdIncomingShipmentCatalogRef { id = item.id.Value };
                continue;
            }

            unresolved.Add(string.IsNullOrWhiteSpace(line.Sku) ? "(missing SKU)" : line.Sku);
        }
        return unresolved;
    }

    private async Task SetContainerTypesAsync(JdIncomingShipmentCreate shipment, CancellationToken cancellationToken)
    {
        if (shipment.lines.Count == 0) return;
        foreach (var line in shipment.lines)
        {
            if (line.inventoryContainerType != null) continue;

            if (line.unit == null) await SetDefaultContainerTypeAsync(shipment, cancellationToken);
    
            var containerType = _containerTypes.FirstOrDefault(ct => string.Equals(ct.name, line.unit, StringComparison.OrdinalIgnoreCase))
                ?? await SetDefaultContainerTypeAsync(shipment, cancellationToken);
            
            if (containerType == null) continue;
            
            line.inventoryContainerType = new JdIncomingShipmentContainerTypeRef { id = containerType.id };
        }
    }

    private async Task<JdContainerType?> SetDefaultContainerTypeAsync(JdIncomingShipmentCreate shipment, CancellationToken cancellationToken)
    {
        var containerType = _containerTypes.FirstOrDefault(ct => string.Equals(ct.name, "Stk", StringComparison.OrdinalIgnoreCase));
        if (containerType == null) return null;
        return containerType;
    }

    /// <summary>
    /// Create a file in JD and return the file information and upload URL.
    /// The caller is responsible for uploading the file content to the pre-signed URL.
    /// </summary>
    /// <param name="displayName">Display name for the file.</param>
    /// <param name="description">File description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upload result with file info and pre-signed URL.</returns>
    public async Task<(bool ok, int status, string message, JdFileResponse? file, string? uploadUrl)> CreateFileAsync(string displayName, string description, CancellationToken cancellationToken)
    {
        try
        {
            // Create file metadata first
            var fileCreate = new JdFileCreate
            {
                displayName = displayName,
                description = description
            };

            var createResult = await repository.CreateFileAsync(fileCreate, cancellationToken);
            if (!createResult.ok)
            {
                return (false, createResult.status, createResult.message, null, null);
            }

            if (createResult.returned == null || string.IsNullOrEmpty(createResult.returned.presignedUrl))
            {
                return (false, createResult.status, "No pre-signed URL received from JD", null, null);
            }

            // Return the file info and upload URL for the caller to use
            return (true, createResult.status, createResult.message, createResult.returned, createResult.returned.presignedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating file in JD");
            return (false, 500, $"Error creating file: {ex.Message}", null, null);
        }
    }

    /// <summary>
    /// Verify a file after it has been uploaded.
    /// </summary>
    /// <param name="fileId">ID of the file to verify.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    public async Task<(bool ok, int status, string message)> VerifyFileAsync(long fileId, CancellationToken cancellationToken)
    {
        var result = await repository.VerifyFileAsync(fileId, cancellationToken);
        if (!result.ok)
        {
            _logger.LogWarning("Failed to verify file {FileId}: {Message}", fileId, result.message);
        }
        return result;
    }
}



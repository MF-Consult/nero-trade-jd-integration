namespace NeroTrade.JDIntegration.Services.ExternalIntegration;

using NeroTrade.JDIntegration.Models.ExternalIntegration;

public interface IJdLogisticsService
{
    Task<UpsertResult<JdAddress>> UpsertAddressesAsync(IEnumerable<JdAddress> addresses, CancellationToken cancellationToken);
    Task<UpsertResult<JdCatalogItem>> UpsertItemsAsync(IEnumerable<JdCatalogItem> items, CancellationToken cancellationToken);
    /// <summary>SKUs on the given shipments' product lines that have no catalog item (with an id) in JD.</summary>
    Task<IReadOnlyList<string>> FindMissingCatalogSkusAsync(IEnumerable<JdIncomingShipmentCreate> shipments, CancellationToken cancellationToken);
    Task<CreateResult<JdIncomingShipmentCreate>> CreateIncomingShipmentsAsync(IEnumerable<JdIncomingShipmentCreate> shipments, CancellationToken cancellationToken);
    Task<IReadOnlyList<JdIncomingShipment>> GetIncomingShipmentsAsync(CancellationToken cancellationToken);
    Task<JdIncomingShipment?> GetIncomingShipmentByIdAsync(long id, CancellationToken cancellationToken);
    Task<UpsertResult<JdInventory>> UpsertInventoriesAsync(IEnumerable<JdInventory> inventories, CancellationToken cancellationToken);

    Task<IReadOnlyList<JdInventory>> GetInventoriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JdRequestOrder>> GetRequestOrdersAsync(long inventoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<JdRequestOrder>> GetRecentRequestOrdersAsync(long inventoryId, DateTime createdAfter, CancellationToken cancellationToken);
    Task<UpsertResult<JdRequestOrderCreate>> UpsertRequestOrdersAsync(long inventoryId, IEnumerable<JdRequestOrderCreate> orders, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message)> DeleteRequestOrderAsync(long inventoryId, long requestOrderId, CancellationToken cancellationToken);

    // File operations
    Task<(bool ok, int status, string message, JdFileResponse? file, string? uploadUrl)> CreateFileAsync(string displayName, string description, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message)> VerifyFileAsync(long fileId, CancellationToken cancellationToken);
}



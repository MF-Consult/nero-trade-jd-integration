namespace NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;

using NeroTrade.JDIntegration.Models.ExternalIntegration;

public interface IJdRepository
{
    Task<IReadOnlyList<JdAddress>> GetAddressesAsync(CancellationToken cancellationToken);
    Task<(bool ok, int status, string message, JdAddress? returned)> CreateAddressAsync(JdAddress address, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message)> UpdateAddressAsync(long id, JdAddress address, CancellationToken cancellationToken);

    Task<IReadOnlyList<JdCatalogItem>> GetCatalogItemsAsync(CancellationToken cancellationToken);
    Task<(bool ok, int status, string message, JdCatalogItem? returned)> CreateCatalogItemAsync(JdCatalogItem item, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message)> UpdateCatalogItemAsync(long id, JdCatalogItem item, CancellationToken cancellationToken);

    // Incoming shipments (purchase orders)
    Task<IReadOnlyList<JdIncomingShipment>> GetIncomingShipmentsAsync(CancellationToken cancellationToken, int? status = 1);
    Task<(bool ok, int status, string message, JdIncomingShipment? returned)> GetIncomingShipmentByIdAsync(long id, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message, JdIncomingShipment? returned)> UpsertIncomingShipmentAsync(JdIncomingShipmentCreate payload, CancellationToken cancellationToken);

    // Container types
    Task<IReadOnlyList<JdContainerType>> GetContainerTypesAsync(CancellationToken cancellationToken);

    // Inventories
    Task<IReadOnlyList<JdInventory>> GetInventoriesAsync(CancellationToken cancellationToken);

    // Request orders
    Task<IReadOnlyList<JdRequestOrder>> GetRequestOrdersAsync(long inventoryId, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message, JdRequestOrder? returned)> CreateRequestOrderAsync(long inventoryId, JdRequestOrderCreate payload, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message)> DeleteRequestOrderAsync(long inventoryId, long requestOrderId, CancellationToken cancellationToken);

    // File operations
    Task<(bool ok, int status, string message, JdFileResponse? returned)> CreateFileAsync(JdFileCreate file, CancellationToken cancellationToken);
    Task<(bool ok, int status, string message)> VerifyFileAsync(long fileId, CancellationToken cancellationToken);

}



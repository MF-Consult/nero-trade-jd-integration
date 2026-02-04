namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;

using Models;

public interface IUnicontaRepository
{
    IAsyncEnumerable<LocalDebtor> ReadAllDebtorsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LocalInventoryItem> ReadAllItemsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LocalPurchaseOrder> ReadAllPurchaseOrdersAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LocalSalesOrder> ReadAllSalesOrdersAsync(CancellationToken cancellationToken);
    
    // Status Sync
    IAsyncEnumerable<LocalSalesOrder> ReadSalesOrdersWithGroupAsync(CancellationToken cancellationToken);
    Task<bool> UpdateSalesOrderGroupAsync(int orderNumber, string group, CancellationToken cancellationToken);

    // Purchase Order Sync
    Task<bool> UpdatePurchaseOrderLineQuantityAsync(int purchaseNumber, string sku, double qtyNow, CancellationToken cancellationToken);
    Task<bool> SetPurchaseOrderHeaderFieldAsync(int purchaseNumber, string fieldName, object value, CancellationToken cancellationToken);

    // File operations - get delivery notes for debtors
    IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync(CancellationToken cancellationToken);
}



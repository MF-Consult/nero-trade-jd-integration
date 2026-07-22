namespace NeroTrade.JDIntegration.Services.UnicontaHandler;

using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public interface IUnicontaService
{
    IAsyncEnumerable<LocalDebtor> ReadDebtorsBatchedAsync(int batchSize, CancellationToken cancellationToken);
    IAsyncEnumerable<LocalInventoryItem> ReadItemsBatchedAsync(int batchSize, CancellationToken cancellationToken);
    IAsyncEnumerable<LocalPurchaseOrder> ReadPurchaseOrdersBatchedAsync(int batchSize, CancellationToken cancellationToken);
    IAsyncEnumerable<LocalSalesOrder> ReadSalesOrdersBatchedAsync(int batchSize, CancellationToken cancellationToken);

    // Status Sync
    IAsyncEnumerable<LocalSalesOrder> ReadSalesOrdersWithGroupAsync(CancellationToken cancellationToken);
    Task<bool> UpdateSalesOrderGroupAsync(int orderNumber, string group, CancellationToken cancellationToken);
    Task<bool> SetSalesOrderStatusAsync(int orderNumber, string group, IReadOnlyDictionary<string, object> userFields, CancellationToken cancellationToken);
    
    // Purchase Order Sync
    Task<bool> UpdatePurchaseOrderLineQuantityAsync(int purchaseNumber, string sku, double qtyNow, CancellationToken cancellationToken);
    Task<bool> SetPurchaseOrderHeaderFieldAsync(int purchaseNumber, string fieldName, object value, CancellationToken cancellationToken);
    Task<bool> SetPurchaseOrderHeaderFieldsAsync(int purchaseNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken);

    // Posted Purchase Invoice Sync (safety-net for orders booked before "Overfør til JD" was set).
    IAsyncEnumerable<LocalPurchaseInvoice> ReadPostedPurchaseInvoicesBatchedAsync(int batchSize, CancellationToken cancellationToken);
    Task<bool> SetPurchaseInvoiceHeaderFieldsAsync(int orderNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken);

    // Inspection (read-only, eligibility-agnostic) — fetch a single order/invoice by number for analysis.
    Task<LocalPurchaseOrder?> ReadPurchaseOrderByNumberAsync(int purchaseNumber, CancellationToken cancellationToken);
    Task<LocalPurchaseInvoice?> ReadPostedPurchaseInvoiceByNumberAsync(int purchaseNumber, CancellationToken cancellationToken);
    Task<LocalSalesOrder?> ReadSalesOrderByNumberAsync(int orderNumber, CancellationToken cancellationToken);

    // File operations - get delivery notes for debtors
    IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync(CancellationToken cancellationToken);
}



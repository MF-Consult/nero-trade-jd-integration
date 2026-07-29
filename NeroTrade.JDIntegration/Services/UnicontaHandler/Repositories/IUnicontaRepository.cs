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
    Task<bool> SetSalesOrderStatusAsync(int orderNumber, string group, IReadOnlyDictionary<string, object> userFields, CancellationToken cancellationToken);

    // Purchase Order Sync
    Task<UnicontaWriteResult> UpdatePurchaseOrderLineQuantityAsync(int purchaseNumber, string sku, double qtyNow, CancellationToken cancellationToken);
    Task<UnicontaWriteResult> SetPurchaseOrderHeaderFieldAsync(int purchaseNumber, string fieldName, object value, CancellationToken cancellationToken);
    Task<UnicontaWriteResult> SetPurchaseOrderHeaderFieldsAsync(int purchaseNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken);

    // Posted Purchase Invoice Sync (safety-net for orders booked before "Overfør til JD" was set).
    IAsyncEnumerable<LocalPurchaseInvoice> ReadPostedPurchaseInvoicesAsync(CancellationToken cancellationToken);
    Task<bool> SetPurchaseInvoiceHeaderFieldsAsync(int orderNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken);

    // Inspection (read-only, eligibility-agnostic) — fetch a single order/invoice by number for analysis.
    Task<LocalPurchaseOrder?> ReadPurchaseOrderByNumberAsync(int purchaseNumber, CancellationToken cancellationToken);
    Task<LocalPurchaseInvoice?> ReadPostedPurchaseInvoiceByNumberAsync(int purchaseNumber, CancellationToken cancellationToken);
    Task<LocalSalesOrder?> ReadSalesOrderByNumberAsync(int orderNumber, CancellationToken cancellationToken);

    // File operations - get delivery notes for debtors
    IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync(CancellationToken cancellationToken);
}



namespace NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;

using Models;

public interface IUnicontaRepository
{
    IAsyncEnumerable<LocalDebtor> ReadAllDebtorsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LocalInventoryItem> ReadAllItemsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LocalPurchaseOrder> ReadAllPurchaseOrdersAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LocalSalesOrder> ReadAllSalesOrdersAsync(CancellationToken cancellationToken);

    // File operations - get delivery notes for debtors
    IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync(CancellationToken cancellationToken);
}



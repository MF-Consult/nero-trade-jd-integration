namespace NeroTrade.JDIntegration.Services.UnicontaHandler;

using Models;
using Repositories;

public sealed class UnicontaService(IUnicontaRepository repo) : IUnicontaService
{
    public async IAsyncEnumerable<LocalDebtor> ReadDebtorsBatchedAsync(int batchSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new List<LocalDebtor>(batchSize);
        await foreach (var d in repo.ReadAllDebtorsAsync(cancellationToken))
        {
            buffer.Add(d);
            if (buffer.Count >= batchSize)
            {
                foreach (var item in buffer)
                    yield return item;
                buffer.Clear();
            }
        }
        foreach (var item in buffer)
            yield return item;
    }

    public async IAsyncEnumerable<LocalInventoryItem> ReadItemsBatchedAsync(int batchSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new List<LocalInventoryItem>(batchSize);
        await foreach (var i in repo.ReadAllItemsAsync(cancellationToken))
        {
            buffer.Add(i);
            if (buffer.Count >= batchSize)
            {
                foreach (var item in buffer)
                    yield return item;
                buffer.Clear();
            }
        }
        foreach (var item in buffer)
            yield return item;
    }

    public async IAsyncEnumerable<LocalPurchaseOrder> ReadPurchaseOrdersBatchedAsync(int batchSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new List<LocalPurchaseOrder>(batchSize);
        await foreach (var o in repo.ReadAllPurchaseOrdersAsync(cancellationToken))
        {
            buffer.Add(o);
            if (buffer.Count >= batchSize)
            {
                foreach (var item in buffer)
                    yield return item;
                buffer.Clear();
            }
        }
        foreach (var item in buffer)
            yield return item;
    }

    public async IAsyncEnumerable<LocalSalesOrder> ReadSalesOrdersBatchedAsync(int batchSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new List<LocalSalesOrder>(batchSize);
        await foreach (var so in repo.ReadAllSalesOrdersAsync(cancellationToken))
        {
            buffer.Add(so);
            if (buffer.Count >= batchSize)
            {
                foreach (var item in buffer)
                    yield return item;
                buffer.Clear();
            }
        }
        foreach (var item in buffer)
            yield return item;
    }

    public async IAsyncEnumerable<LocalSalesOrder> ReadSalesOrdersWithGroupAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var order in repo.ReadSalesOrdersWithGroupAsync(cancellationToken))
        {
            yield return order;
        }
    }

    public Task<bool> UpdateSalesOrderGroupAsync(int orderNumber, string group, CancellationToken cancellationToken)
    {
        return repo.UpdateSalesOrderGroupAsync(orderNumber, group, cancellationToken);
    }

    public Task<bool> UpdatePurchaseOrderLineQuantityAsync(int purchaseNumber, string sku, double qtyNow, CancellationToken cancellationToken)
    {
        return repo.UpdatePurchaseOrderLineQuantityAsync(purchaseNumber, sku, qtyNow, cancellationToken);
    }

    public Task<bool> SetPurchaseOrderHeaderFieldAsync(int purchaseNumber, string fieldName, object value, CancellationToken cancellationToken)
    {
        return repo.SetPurchaseOrderHeaderFieldAsync(purchaseNumber, fieldName, value, cancellationToken);
    }

    public Task<bool> SetSalesOrderHeaderFieldAsync(int orderNumber, string fieldName, object value, CancellationToken cancellationToken)
    {
        return repo.SetSalesOrderHeaderFieldAsync(orderNumber, fieldName, value, cancellationToken);
    }

    public async IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var note in repo.ReadDebtorDeliveryNotesAsync(cancellationToken))
        {
            yield return note;
        }
    }
}



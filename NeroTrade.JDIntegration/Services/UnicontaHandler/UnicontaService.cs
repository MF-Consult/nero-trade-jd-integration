namespace NeroTrade.JDIntegration.Services.UnicontaHandler;

using Microsoft.Extensions.Logging;
using Models;
using NeroTrade.JDIntegration.Models.Settings;
using Repositories;

public sealed class UnicontaService(IUnicontaRepository repo, JdSettings jdSettings, ILogger<UnicontaService> logger) : IUnicontaService
{
    // DryRun is the integration-wide "mutate nothing" switch (env var JD__DryRun). It already blocks
    // every mutating JD call in JdRepository; we guard the Uniconta writes here too so a dry run is
    // fully side-effect free. Without this guard a dry run still wrote order-status fields back to
    // Uniconta — most damagingly marking just-pushed sales orders "Oprettet", so they were then
    // skipped on the real run (filter requires Group empty or "Fejlet"). Mutating methods short-circuit
    // to success (Task.FromResult(true)) so the caller's happy path and the JD payload preview complete
    // unchanged, mirroring JdRepository's synthetic 200 OK. Reads are never guarded.
    private Task<bool> DryRunSkipUnicontaWrite(string operation)
    {
        logger.LogInformation("[DRY-RUN] Skipping Uniconta write {Operation} — nothing written to Uniconta.", operation);
        return Task.FromResult(true);
    }
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
        if (jdSettings.DryRun) return DryRunSkipUnicontaWrite($"UpdateSalesOrderGroup(order={orderNumber}, group={group})");
        return repo.UpdateSalesOrderGroupAsync(orderNumber, group, cancellationToken);
    }

    public Task<bool> SetSalesOrderStatusAsync(int orderNumber, string group, IReadOnlyDictionary<string, object> userFields, CancellationToken cancellationToken)
    {
        if (jdSettings.DryRun) return DryRunSkipUnicontaWrite($"SetSalesOrderStatus(order={orderNumber}, group={group})");
        return repo.SetSalesOrderStatusAsync(orderNumber, group, userFields, cancellationToken);
    }

    public Task<bool> UpdatePurchaseOrderLineQuantityAsync(int purchaseNumber, string sku, double qtyNow, CancellationToken cancellationToken)
    {
        if (jdSettings.DryRun) return DryRunSkipUnicontaWrite($"UpdatePurchaseOrderLineQuantity(po={purchaseNumber}, sku={sku}, qty={qtyNow})");
        return repo.UpdatePurchaseOrderLineQuantityAsync(purchaseNumber, sku, qtyNow, cancellationToken);
    }

    public Task<bool> SetPurchaseOrderHeaderFieldAsync(int purchaseNumber, string fieldName, object value, CancellationToken cancellationToken)
    {
        if (jdSettings.DryRun) return DryRunSkipUnicontaWrite($"SetPurchaseOrderHeaderField(po={purchaseNumber}, field={fieldName})");
        return repo.SetPurchaseOrderHeaderFieldAsync(purchaseNumber, fieldName, value, cancellationToken);
    }

    public Task<bool> SetPurchaseOrderHeaderFieldsAsync(int purchaseNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken)
    {
        if (jdSettings.DryRun) return DryRunSkipUnicontaWrite($"SetPurchaseOrderHeaderFields(po={purchaseNumber}, fields=[{string.Join(",", fields.Keys)}])");
        return repo.SetPurchaseOrderHeaderFieldsAsync(purchaseNumber, fields, cancellationToken);
    }

    public async IAsyncEnumerable<LocalPurchaseInvoice> ReadPostedPurchaseInvoicesBatchedAsync(int batchSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new List<LocalPurchaseInvoice>(batchSize);
        await foreach (var inv in repo.ReadPostedPurchaseInvoicesAsync(cancellationToken))
        {
            buffer.Add(inv);
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

    public Task<bool> SetPurchaseInvoiceHeaderFieldsAsync(int orderNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken)
    {
        if (jdSettings.DryRun) return DryRunSkipUnicontaWrite($"SetPurchaseInvoiceHeaderFields(order={orderNumber}, fields=[{string.Join(",", fields.Keys)}])");
        return repo.SetPurchaseInvoiceHeaderFieldsAsync(orderNumber, fields, cancellationToken);
    }

    // Inspection (read-only) — no DryRun gating; these never mutate.
    public Task<LocalPurchaseOrder?> ReadPurchaseOrderByNumberAsync(int purchaseNumber, CancellationToken cancellationToken)
        => repo.ReadPurchaseOrderByNumberAsync(purchaseNumber, cancellationToken);

    public Task<LocalPurchaseInvoice?> ReadPostedPurchaseInvoiceByNumberAsync(int purchaseNumber, CancellationToken cancellationToken)
        => repo.ReadPostedPurchaseInvoiceByNumberAsync(purchaseNumber, cancellationToken);

    public Task<LocalSalesOrder?> ReadSalesOrderByNumberAsync(int orderNumber, CancellationToken cancellationToken)
        => repo.ReadSalesOrderByNumberAsync(orderNumber, cancellationToken);

    public async IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var note in repo.ReadDebtorDeliveryNotesAsync(cancellationToken))
        {
            yield return note;
        }
    }
}



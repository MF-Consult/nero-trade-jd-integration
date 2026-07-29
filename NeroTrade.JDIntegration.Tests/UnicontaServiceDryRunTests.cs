using Microsoft.Extensions.Logging.Abstractions;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;
using Xunit;

// The Uniconta SDK exposes a top-level `UnicontaService` namespace that shadows our class by simple
// name, so reference the class through an alias.
using UnicontaSvc = NeroTrade.JDIntegration.Services.UnicontaHandler.UnicontaService;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the DryRun guard added 2026-06-17. With JD__DryRun on, no <see cref="UnicontaService"/>
/// mutating method may reach the repository — a payload preview must never write order status back
/// to Uniconta — yet each still reports success so the caller's happy path and the JD payload
/// preview complete unchanged. With DryRun off, every write delegates to the repository.
/// Regression cover for the incident where a dry run marked previewed sales orders "Oprettet",
/// after which the real run skipped them (eligibility filter requires Group empty or "Fejlet").
/// </summary>
public class UnicontaServiceDryRunTests
{
    [Fact]
    public async Task DryRun_SalesAndPurchaseWrites_DoNotReachRepository_ButReportSuccess()
    {
        var spy = new SpyRepository();
        var service = BuildService(spy, dryRun: true);

        Assert.True(await service.UpdateSalesOrderGroupAsync(2208, "", default));
        Assert.True(await service.SetSalesOrderStatusAsync(2208, "Oprettet", new Dictionary<string, object>(), default));
        Assert.Equal(UnicontaWriteResult.Updated, await service.UpdatePurchaseOrderLineQuantityAsync(100, "SKU1", 3, default));
        Assert.Equal(UnicontaWriteResult.Updated, await service.SetPurchaseOrderHeaderFieldAsync(100, "xField", "v", default));
        Assert.Equal(UnicontaWriteResult.Updated, await service.SetPurchaseOrderHeaderFieldsAsync(100, new Dictionary<string, object>(), default));
        Assert.True(await service.SetPurchaseInvoiceHeaderFieldsAsync(32, new Dictionary<string, object>(), default));

        Assert.Equal(0, spy.WriteCallCount);
    }

    [Fact]
    public async Task LiveRun_SalesAndPurchaseWrites_DelegateToRepository()
    {
        var spy = new SpyRepository();
        var service = BuildService(spy, dryRun: false);

        await service.UpdateSalesOrderGroupAsync(2208, "", default);
        await service.SetSalesOrderStatusAsync(2208, "Oprettet", new Dictionary<string, object>(), default);
        await service.UpdatePurchaseOrderLineQuantityAsync(100, "SKU1", 3, default);
        await service.SetPurchaseOrderHeaderFieldAsync(100, "xField", "v", default);
        await service.SetPurchaseOrderHeaderFieldsAsync(100, new Dictionary<string, object>(), default);
        await service.SetPurchaseInvoiceHeaderFieldsAsync(32, new Dictionary<string, object>(), default);

        Assert.Equal(6, spy.WriteCallCount);
    }

    private static UnicontaSvc BuildService(IUnicontaRepository repo, bool dryRun) =>
        new(repo, new JdSettings { DryRun = dryRun }, NullLogger<UnicontaSvc>.Instance);

    /// <summary>Records how many mutating repository methods were invoked; reads are inert.</summary>
    private sealed class SpyRepository : IUnicontaRepository
    {
        public int WriteCallCount { get; private set; }

        public Task<bool> UpdateSalesOrderGroupAsync(int orderNumber, string group, CancellationToken cancellationToken)
        { WriteCallCount++; return Task.FromResult(true); }

        public Task<bool> SetSalesOrderStatusAsync(int orderNumber, string group, IReadOnlyDictionary<string, object> userFields, CancellationToken cancellationToken)
        { WriteCallCount++; return Task.FromResult(true); }

        public Task<UnicontaWriteResult> UpdatePurchaseOrderLineQuantityAsync(int purchaseNumber, string sku, double qtyNow, CancellationToken cancellationToken)
        { WriteCallCount++; return Task.FromResult(UnicontaWriteResult.Updated); }

        public Task<UnicontaWriteResult> SetPurchaseOrderHeaderFieldAsync(int purchaseNumber, string fieldName, object value, CancellationToken cancellationToken)
        { WriteCallCount++; return Task.FromResult(UnicontaWriteResult.Updated); }

        public Task<UnicontaWriteResult> SetPurchaseOrderHeaderFieldsAsync(int purchaseNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken)
        { WriteCallCount++; return Task.FromResult(UnicontaWriteResult.Updated); }

        public Task<bool> SetPurchaseInvoiceHeaderFieldsAsync(int orderNumber, IReadOnlyDictionary<string, object> fields, CancellationToken cancellationToken)
        { WriteCallCount++; return Task.FromResult(true); }

        // Reads are never exercised by these tests — return empty sequences.
        public IAsyncEnumerable<LocalDebtor> ReadAllDebtorsAsync(CancellationToken cancellationToken) => EmptyAsync<LocalDebtor>();
        public IAsyncEnumerable<LocalInventoryItem> ReadAllItemsAsync(CancellationToken cancellationToken) => EmptyAsync<LocalInventoryItem>();
        public IAsyncEnumerable<LocalPurchaseOrder> ReadAllPurchaseOrdersAsync(CancellationToken cancellationToken) => EmptyAsync<LocalPurchaseOrder>();
        public IAsyncEnumerable<LocalPurchaseInvoice> ReadPostedPurchaseInvoicesAsync(CancellationToken cancellationToken) => EmptyAsync<LocalPurchaseInvoice>();
        public IAsyncEnumerable<LocalSalesOrder> ReadAllSalesOrdersAsync(CancellationToken cancellationToken) => EmptyAsync<LocalSalesOrder>();
        public IAsyncEnumerable<LocalSalesOrder> ReadSalesOrdersWithGroupAsync(CancellationToken cancellationToken) => EmptyAsync<LocalSalesOrder>();
        public IAsyncEnumerable<DebtorDeliveryNoteInfo> ReadDebtorDeliveryNotesAsync(CancellationToken cancellationToken) => EmptyAsync<DebtorDeliveryNoteInfo>();

        // Inspection reads — inert here.
        public Task<LocalPurchaseOrder?> ReadPurchaseOrderByNumberAsync(int purchaseNumber, CancellationToken cancellationToken) => Task.FromResult<LocalPurchaseOrder?>(null);
        public Task<LocalPurchaseInvoice?> ReadPostedPurchaseInvoiceByNumberAsync(int purchaseNumber, CancellationToken cancellationToken) => Task.FromResult<LocalPurchaseInvoice?>(null);
        public Task<LocalSalesOrder?> ReadSalesOrderByNumberAsync(int orderNumber, CancellationToken cancellationToken) => Task.FromResult<LocalSalesOrder?>(null);

        private static async IAsyncEnumerable<T> EmptyAsync<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

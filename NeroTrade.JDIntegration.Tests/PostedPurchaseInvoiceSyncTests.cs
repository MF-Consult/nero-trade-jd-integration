using Microsoft.Extensions.Logging.Abstractions;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the posted-purchase-invoice safety-net contract. The safety-net catches purchase orders booked
/// before "Overfør til JD" was set. It must emit the SAME "PO {originatingOrderNumber}" identity as the
/// open-order flow so JD's existing-shipment dedup skips orders already sent (no duplicates), while still
/// creating the genuinely-missed ones.
/// </summary>
public class PostedPurchaseInvoiceSyncTests
{
    [Fact]
    public void Map_PostedInvoice_SetsTextToOriginatingOrderNumber()
    {
        // Dedup parity with the open-order flow depends on this exact identity.
        var shipment = MapInvoice(orderNumber: 99, invoiceNumber: 133398, ("ITEM-1", "PC-001"));

        Assert.Equal("PO 99", shipment.text);
        Assert.Equal(99, shipment.SourcePurchaseNumber);
        var line = Assert.Single(shipment.lines);
        Assert.Equal("ITEM-1", line.Sku);
    }

    [Fact]
    public async Task CreateIncomingShipments_OrderAlreadyInJd_SkipsCreateButCountsSuccess()
    {
        // Order 99 was already sent to JD (by the open-order flow or an earlier tick).
        var repo = new FakeJdRepository
        {
            CatalogItems = { new JdCatalogItem { id = 555, sku = "ITEM-1" } },
            ExistingShipments = { new JdIncomingShipment { text = "PO 99" } }
        };
        var service = BuildService(repo);

        var shipment = MapInvoice(99, 133398, ("ITEM-1", "PC-001"));

        var result = await service.CreateIncomingShipmentsAsync(new[] { shipment }, CancellationToken.None);

        Assert.Equal(1, result.SuccessCount);
        Assert.Empty(result.Failures);
        Assert.Empty(repo.SentShipments); // deduped — no duplicate create
        Assert.Single(result.CreatedItems); // still reported so the caller flags it in Uniconta
    }

    [Fact]
    public async Task CreateIncomingShipments_OrderNotInJd_CreatesShipment()
    {
        var repo = new FakeJdRepository
        {
            CatalogItems = { new JdCatalogItem { id = 555, sku = "ITEM-1" } }
        };
        var service = BuildService(repo);

        var shipment = MapInvoice(100, 200000, ("ITEM-1", "PC-001"));

        var result = await service.CreateIncomingShipmentsAsync(new[] { shipment }, CancellationToken.None);

        Assert.Equal(1, result.SuccessCount);
        var sent = Assert.Single(repo.SentShipments);
        Assert.Equal("PO 100", sent.text);
        Assert.Equal(555, Assert.Single(sent.lines).catalog!.id);
    }

    [Fact]
    public async Task CreateIncomingShipments_InvoiceSkuMissingFromCatalog_FailsAndSendsNothing()
    {
        var repo = new FakeJdRepository(); // empty catalog
        var service = BuildService(repo);

        var shipment = MapInvoice(101, 300000, ("UNKNOWN-SKU", null));

        var result = await service.CreateIncomingShipmentsAsync(new[] { shipment }, CancellationToken.None);

        Assert.Equal(0, result.SuccessCount);
        Assert.Empty(repo.SentShipments);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("UNKNOWN-SKU", failure.Message);
    }

    private static JdIncomingShipmentCreate MapInvoice(int orderNumber, long invoiceNumber, params (string sku, string? externalSku)[] lines)
    {
        var invoice = new LocalPurchaseInvoice { PurchaseNumber = orderNumber, InvoiceNumber = invoiceNumber };
        foreach (var (sku, externalSku) in lines)
        {
            invoice.Lines.Add(new LocalPurchaseInvoiceLine
            {
                Sku = sku,
                Quantity = 1,
                IsSubItem = true,
                Unit = "Stk",
                CustomerItemNumber = externalSku
            });
        }
        return new PurchaseOrderMapper().Map(invoice);
    }

    private static JdLogisticsService BuildService(FakeJdRepository repo) =>
        new(repo, new JdReadCache(), new NoOpIntegrationLogger(), new SupabaseOptions(),
            NullLogger<JdLogisticsService>.Instance);

    private sealed class NoOpIntegrationLogger : IIntegrationLogger
    {
        public string IntegrationName => "NeroTrade.JDIntegration.Tests";
        public Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
        public IntegrationRun BeginRun(string runName) => throw new NotSupportedException();
        public Task MarkResolvedAsync(string integrationName, string externalId, Guid successCorrelationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeJdRepository : IJdRepository
    {
        public List<JdCatalogItem> CatalogItems { get; } = new();
        public List<JdIncomingShipment> ExistingShipments { get; } = new();
        public List<JdIncomingShipmentCreate> SentShipments { get; } = new();

        public Task<IReadOnlyList<JdCatalogItem>> GetCatalogItemsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<JdCatalogItem>>(CatalogItems);

        public Task<IReadOnlyList<JdContainerType>> GetContainerTypesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<JdContainerType>>(new List<JdContainerType> { new() { id = 1, name = "Stk" } });

        public Task<IReadOnlyList<JdIncomingShipment>> GetIncomingShipmentsAsync(CancellationToken cancellationToken, int? status = 1)
            => Task.FromResult<IReadOnlyList<JdIncomingShipment>>(ExistingShipments);

        public Task<(bool ok, int status, string message, JdIncomingShipment? returned)> UpsertIncomingShipmentAsync(JdIncomingShipmentCreate payload, CancellationToken cancellationToken)
        {
            SentShipments.Add(payload);
            return Task.FromResult((true, 200, "ok", (JdIncomingShipment?)new JdIncomingShipment { id = 1 }));
        }

        // Unused by these tests.
        public Task<IReadOnlyList<JdAddress>> GetAddressesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message, JdAddress? returned)> CreateAddressAsync(JdAddress address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message)> UpdateAddressAsync(long id, JdAddress address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message, JdCatalogItem? returned)> CreateCatalogItemAsync(JdCatalogItem item, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message)> UpdateCatalogItemAsync(long id, JdCatalogItem item, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message, JdIncomingShipment? returned)> GetIncomingShipmentByIdAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<JdInventory>> GetInventoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<JdRequestOrder>> GetRequestOrdersAsync(long inventoryId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message, JdRequestOrder? returned)> CreateRequestOrderAsync(long inventoryId, JdRequestOrderCreate payload, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message)> DeleteRequestOrderAsync(long inventoryId, long requestOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message, JdFileResponse? returned)> CreateFileAsync(JdFileCreate file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool ok, int status, string message)> VerifyFileAsync(long fileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(int status, string body)> GetRawAsync(string relativePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

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
/// Pins the unit → JD container-type contract after the 2026-07 "everything arrives as Stk" incident.
///
/// Uniconta returns a line's unit as the English <c>ItemUnit</c> enum name ("Packages"), while JD's
/// container types carry the Danish label the Uniconta UI shows the user ("Kolli"). The resolution in
/// <see cref="JdLogisticsService"/> is an exact name match, so untranslated units never matched and
/// every line silently fell back to Stk — confirmed on PO 33/37/39 (Uniconta <c>Unit = "Packages"</c>,
/// JD <c>inventoryContainerType = Stk</c>).
/// </summary>
public class UnitTranslationTests
{
    [Theory]
    [InlineData("Packages", "Kolli")]   // the one that broke: kolli
    [InlineData("Pcs", "Stk")]
    [InlineData("Pallet", "Palle")]
    [InlineData("Container", "Container")]
    [InlineData("packages", "Kolli")]   // case-insensitive
    [InlineData("  Packages  ", "Kolli")]
    public void UnicontaUnit_TranslatesToJdContainerTypeName(string unicontaUnit, string expected)
        => Assert.Equal(expected, UnitTranslator.ToJdContainerTypeName(unicontaUnit));

    [Theory]
    [InlineData("Palle", "Palle")]      // free-text xEnhedstype is already Danish — pass through
    [InlineData("Kolli", "Kolli")]
    [InlineData("kg", "kg")]            // no JD counterpart — pass through, reported downstream
    public void UnitWithoutTranslation_PassesThroughUnchanged(string unit, string expected)
        => Assert.Equal(expected, UnitTranslator.ToJdContainerTypeName(unit));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankUnit_ResolvesToNull_SoJdDefaultApplies(string? unit)
        => Assert.Null(UnitTranslator.ToJdContainerTypeName(unit));

    [Fact]
    public void Mapper_TranslatesLineUnit_AndLeavesDanishContainerParentAlone()
    {
        var po = new LocalPurchaseOrder
        {
            PurchaseNumber = 37,
            ContainerType = "Palle",
            ContainerCount = 1
        };
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "ITEM-1", Quantity = 13, Unit = "Packages" });

        var shipment = new PurchaseOrderMapper().Map(po);

        Assert.Equal("Palle", shipment.lines[0].unit);   // container parent, free text, untouched
        Assert.Equal("Kolli", shipment.lines[1].unit);   // product line, translated from "Packages"
    }

    [Fact]
    public void PostedInvoiceMapper_TranslatesLineUnit_Identically()
    {
        // The safety-net path must not drift from the open-order path.
        var invoice = new LocalPurchaseInvoice { PurchaseNumber = 37, InvoiceNumber = 108838487 };
        invoice.Lines.Add(new LocalPurchaseInvoiceLine { Sku = "ITEM-1", Quantity = 13, Unit = "Packages" });

        var shipment = new PurchaseOrderMapper().Map(invoice);

        Assert.Equal("Kolli", Assert.Single(shipment.lines).unit);
    }

    [Fact]
    public async Task PackagesLine_IsSentToJdAsKolli_NotStk()
    {
        var repo = new FakeRepo { CatalogItems = { new JdCatalogItem { id = 555, sku = "ITEM-1" } } };
        var logger = new RecordingIntegrationLogger();
        var service = BuildService(repo, logger);

        var po = new LocalPurchaseOrder { PurchaseNumber = 200 };
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "ITEM-1", Quantity = 13, Unit = "Packages" });

        var result = await service.CreateIncomingShipmentsAsync(new[] { new PurchaseOrderMapper().Map(po) }, CancellationToken.None);

        Assert.Equal(1, result.SuccessCount);
        var sent = Assert.Single(repo.SentShipments);
        Assert.Equal(FakeRepo.KolliId, Assert.Single(sent.lines).inventoryContainerType!.id);
        Assert.Empty(logger.Entries); // a mapped unit is not a warning
    }

    [Fact]
    public async Task UnmappedUnit_StillFallsBackToStk_ButIsReported()
    {
        // "kg" has no JD container type. The shipment must still go through (the warehouse needs it),
        // but the degradation must be visible in integration_logs instead of silent.
        var repo = new FakeRepo { CatalogItems = { new JdCatalogItem { id = 555, sku = "ITEM-1" } } };
        var logger = new RecordingIntegrationLogger();
        var service = BuildService(repo, logger);

        var po = new LocalPurchaseOrder { PurchaseNumber = 201 };
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "ITEM-1", Quantity = 2, Unit = "kg" });

        var result = await service.CreateIncomingShipmentsAsync(new[] { new PurchaseOrderMapper().Map(po) }, CancellationToken.None);

        Assert.Equal(1, result.SuccessCount);
        var sent = Assert.Single(repo.SentShipments);
        Assert.Equal(FakeRepo.StkId, Assert.Single(sent.lines).inventoryContainerType!.id);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal("JD_CONTAINER_TYPE_UNMAPPED", warning.ErrorCode);
        Assert.Equal("warning", warning.Level);
        Assert.Contains("kg", warning.Message);
        Assert.Equal("PO 201", warning.ExternalId);
    }

    private static JdLogisticsService BuildService(FakeRepo repo, IIntegrationLogger logger) =>
        new(repo, new JdReadCache(), logger, new SupabaseOptions(), NullLogger<JdLogisticsService>.Instance);

    private sealed class RecordingIntegrationLogger : IIntegrationLogger
    {
        public List<IntegrationLogEntry> Entries { get; } = new();
        public string IntegrationName => "NeroTrade.JDIntegration.Tests";
        public Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public IntegrationRun BeginRun(string runName) => throw new NotSupportedException();
        public Task MarkResolvedAsync(string integrationName, string externalId, Guid successCorrelationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Container types mirror the real JD account (GET /api/containertypes, 2026-07-27).
    private sealed class FakeRepo : IJdRepository
    {
        public const long ContainerId = 1;
        public const long PalleId = 3;
        public const long KolliId = 13;
        public const long StkId = 15;

        public List<JdCatalogItem> CatalogItems { get; } = new();
        public List<JdIncomingShipmentCreate> SentShipments { get; } = new();

        public Task<IReadOnlyList<JdCatalogItem>> GetCatalogItemsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<JdCatalogItem>>(CatalogItems);

        public Task<IReadOnlyList<JdContainerType>> GetContainerTypesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<JdContainerType>>(new List<JdContainerType>
            {
                new() { id = ContainerId, name = "Container" },
                new() { id = PalleId, name = "Palle" },
                new() { id = KolliId, name = "Kolli" },
                new() { id = StkId, name = "Stk" },
            });

        public Task<IReadOnlyList<JdIncomingShipment>> GetIncomingShipmentsAsync(CancellationToken cancellationToken, int? status = 1)
            => Task.FromResult<IReadOnlyList<JdIncomingShipment>>(new List<JdIncomingShipment>());

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

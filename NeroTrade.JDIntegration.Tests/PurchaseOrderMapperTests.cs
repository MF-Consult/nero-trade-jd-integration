using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the purchase-order → incoming-shipment header mapping: carrier, expected delivery date and
/// remark come from Uniconta (xCarrier / _DeliveryDate / xRemarksForJD) with the original hardcoded
/// values as fallbacks. Critically, <c>text</c> must always START with the machine key "PO {n}",
/// because JdOrderHelper parses the PO number back out of it for dedup and received-quantity sync.
/// </summary>
public class PurchaseOrderMapperTests
{
    [Fact]
    public void Map_UsesCarrierDeliveryDateAndRemark_WhenPopulated()
    {
        var deliveryDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var po = new LocalPurchaseOrder
        {
            PurchaseNumber = 2200,
            Carrier = "DSV",
            DeliveryDate = deliveryDate,
            RemarkText = "Levering bagindgang"
        };

        var create = new PurchaseOrderMapper().Map(po);

        Assert.Equal("DSV", create.carrier);
        Assert.Equal(deliveryDate, create.date);
        Assert.Equal("PO 2200 - Levering bagindgang", create.text);
    }

    [Fact]
    public void Map_FallsBackToDefaults_WhenFieldsAreMissing()
    {
        var before = DateTime.UtcNow.AddDays(2);
        var create = new PurchaseOrderMapper().Map(new LocalPurchaseOrder { PurchaseNumber = 2201 });
        var after = DateTime.UtcNow.AddDays(2);

        Assert.Equal("TBD", create.carrier);
        Assert.NotNull(create.date);
        Assert.InRange(create.date!.Value, before, after);
        Assert.Equal("PO 2201", create.text);
    }

    [Fact]
    public void Map_TreatsWhitespaceAsMissing_AndTrimsValues()
    {
        var po = new LocalPurchaseOrder
        {
            PurchaseNumber = 2202,
            Carrier = "   ",
            RemarkText = "  haster  "
        };

        var create = new PurchaseOrderMapper().Map(po);

        Assert.Equal("TBD", create.carrier);
        Assert.Equal("PO 2202 - haster", create.text);
    }

    [Theory]
    [InlineData("PO 2200", 2200)]
    [InlineData("PO 2200 - Levering bagindgang", 2200)]
    [InlineData("PO 2200 - levering torsdag kl 14", 2200)]
    [InlineData("PO 2200 - vedrører PO 9999", 2200)] // leftmost match wins even if the remark mentions another PO
    public void GetPurchaseOrderNumber_ParsesPoNumber_WithAndWithoutRemark(string text, int expected)
    {
        Assert.Equal(expected, JdOrderHelper.GetPurchaseOrderNumber(text));
    }

    [Fact]
    public void Map_TextRoundTripsThroughPoNumberParsing()
    {
        var po = new LocalPurchaseOrder { PurchaseNumber = 2203, RemarkText = "ring 30 min før" };

        var create = new PurchaseOrderMapper().Map(po);

        Assert.StartsWith("PO 2203", create.text);
        Assert.Equal(2203, JdOrderHelper.GetPurchaseOrderNumber(create.text));
    }
}

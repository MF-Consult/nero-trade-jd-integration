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

    // --- Lagerhotel container parent / sub-item structure ---
    // JD expects a pallet shipment as a parent (isSubItem=false, the container) with the products as
    // children (isSubItem=true). Without the container fields the products go as a flat list. This
    // replaces the old behaviour where every line was hardcoded isSubItem=true with no parent.

    [Fact]
    public void Map_WithoutContainerFields_SendsFlatProductLines_NoParent()
    {
        var po = new LocalPurchaseOrder { PurchaseNumber = 2300 };
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "A", Quantity = 2, Unit = "Stk" });
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "B", Quantity = 1, Unit = "Stk" });

        var create = new PurchaseOrderMapper().Map(po);

        Assert.Equal(2, create.lines.Count);
        Assert.All(create.lines, l => Assert.False(l.isSubItem!.Value));
        Assert.All(create.lines, l => Assert.False(string.IsNullOrEmpty(l.Sku)));
    }

    [Fact]
    public void Map_WithContainerFields_EmitsContainerParentThenChildren()
    {
        var po = new LocalPurchaseOrder
        {
            PurchaseNumber = 2301,
            ContainerType = "Palle",
            ContainerCount = 3
        };
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "A", Quantity = 5, Unit = "Stk" });
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "B", Quantity = 2, Unit = "Stk" });

        var create = new PurchaseOrderMapper().Map(po);

        Assert.Equal(3, create.lines.Count);

        var parent = create.lines[0];
        Assert.False(parent.isSubItem!.Value);   // the container itself
        Assert.Null(parent.Sku);                  // pure container, no catalog item
        Assert.Equal(3, parent.quantity);         // antal paller
        Assert.Equal("Palle", parent.unit);       // resolved to JD container type downstream

        Assert.All(create.lines.Skip(1), child =>
        {
            Assert.True(child.isSubItem!.Value);  // products hang under the parent
            Assert.False(string.IsNullOrEmpty(child.Sku));
        });
    }

    [Fact]
    public void Map_DoesNotSerializeInternalKeys_ToJdPayload()
    {
        // unit (container-type/unit resolution key) and Sku are internal only — they are NOT part of
        // JD's IncomingShipmentLineRbo schema and must never reach the payload (they are consumed in
        // memory to fill inventoryContainerType / catalog before the shipment is serialized).
        var po = new LocalPurchaseOrder { PurchaseNumber = 2400, ContainerType = "Palle", ContainerCount = 1 };
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "A", Quantity = 1, Unit = "Stk" });

        var json = System.Text.Json.JsonSerializer.Serialize(new PurchaseOrderMapper().Map(po));

        Assert.DoesNotContain("\"unit\"", json);
        Assert.DoesNotContain("\"Sku\"", json);
        Assert.Contains("\"isSubItem\"", json);
        Assert.Contains("\"quantity\"", json);
    }

    [Theory]
    [InlineData("Palle", 0)]   // type chosen but no count
    [InlineData(null, 3)]      // count but no type
    [InlineData("", 3)]        // blank type
    public void Map_TreatsIncompleteContainerFields_AsFlatList(string? containerType, double count)
    {
        var po = new LocalPurchaseOrder
        {
            PurchaseNumber = 2302,
            ContainerType = containerType,
            ContainerCount = count
        };
        po.Lines.Add(new LocalPurchaseOrderLine { Sku = "A", Quantity = 1, Unit = "Stk" });

        var create = new PurchaseOrderMapper().Map(po);

        var line = Assert.Single(create.lines);
        Assert.False(line.isSubItem!.Value);
        Assert.Equal("A", line.Sku);
    }
}

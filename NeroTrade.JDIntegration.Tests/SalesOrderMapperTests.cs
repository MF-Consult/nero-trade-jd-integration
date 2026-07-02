using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the sales-order note routing — each Uniconta note lands in its own JD field:
/// <c>text</c> ("Intern Note") = "SO {n}" + the xRemarksForJD remark after " - ";
/// <c>trackingNote</c> = xTrackingNote (Sporingsnote), its own field;
/// <c>deliveryNoteText</c> ("Note på følgeseddel") = label text only (no UC order number).
/// JdOrderHelper reads the "SO {n}" key back from text (leading, so the leftmost match wins).
/// </summary>
public class SalesOrderMapperTests
{
    [Fact]
    public void Map_PutsSoKeyAndRemarkInText_DashSeparated()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2193,
            RemarkText = "Hej Mikkel, kommer min bemærkning med over?"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("SO 2193 - Hej Mikkel, kommer min bemærkning med over?", create.text);
        Assert.Null(create.trackingNote);
        Assert.Null(create.deliveryNoteText);
    }

    [Fact]
    public void Map_RoutesSporingsnoteToItsOwnTrackingNoteField()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2194,
            TrackingNote = "Quattro Fontane Due",
            RemarkText = "  Ring 30 min før  "
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("SO 2194 - Ring 30 min før", create.text);       // Intern Note
        Assert.Equal("Quattro Fontane Due", create.trackingNote);     // Sporingsnote, its own field
        Assert.Null(create.deliveryNoteText);
    }

    [Fact]
    public void Map_WithoutRemark_TextIsJustSoKey_NoDanglingSeparator()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2195,
            TrackingNote = "Quattro Fontane Due"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("SO 2195", create.text);
        Assert.Equal("Quattro Fontane Due", create.trackingNote);
        Assert.Null(create.deliveryNoteText);
    }

    [Fact]
    public void Map_KeepsLabelTextOnDeliveryNote_WithoutSoKey()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2196,
            DeliveryNoteText = "Label-tekst til følgeseddel",
            RemarkText = "intern besked"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("Label-tekst til følgeseddel", create.deliveryNoteText); // no "SO {n}" on the label
        Assert.Equal("SO 2196 - intern besked", create.text);
    }

    [Fact]
    public void Map_Text_RoundTripsThroughOrderNumberParsing()
    {
        var so = new LocalSalesOrder { OrderNumber = 2197, RemarkText = "vedrører SO 9999" };

        var create = new SalesOrderMapper().Map(so);

        // The key leads text, so the leftmost regex match wins even though the remark in the same
        // field mentions another SO (9999). Identification reads text (then deliveryNoteText fallback).
        Assert.Equal(2197, JdOrderHelper.GetOrderNumber(create.shopOrderId, create.text, create.deliveryNoteText));
    }

    // --- PL_EXCHANGE (byttepaller) is opt-in per order via xByttepaller / LocalSalesOrder.ExchangePallets ---

    [Fact]
    public void Map_AddsPalletExchange_ForPalletOrder_WhenExchangePalletsTrue()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 3001,
            DeliveryType = "Palle Fragt", // → glimoe / GLIMOE_PARCEL (supports PL_EXCHANGE)
            ExchangePallets = true
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.NotNull(create.shipmondo);
        Assert.Contains(ShipmondoServiceCodes.PalletExchange, create.shipmondo!.productServices!);
    }

    [Fact]
    public void Map_DoesNotAddPalletExchange_ForPalletOrder_WhenExchangePalletsFalse()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 3002,
            DeliveryType = "Palle Fragt", // pallet product, but byttepaller not chosen
            ExchangePallets = false
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.NotNull(create.shipmondo);
        Assert.DoesNotContain(ShipmondoServiceCodes.PalletExchange, create.shipmondo!.productServices!);
    }

    [Fact]
    public void Map_NeverAddsPalletExchange_ForGlsParcel_EvenWhenExchangePalletsTrue()
    {
        // Safety net: the product allow-list rejects PL_EXCHANGE for GLS parcels regardless of the flag.
        var so = new LocalSalesOrder
        {
            OrderNumber = 3003,
            DeliveryType = "GLS", // → gls / GLSDK_BP (no services allowed)
            ExchangePallets = true
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.NotNull(create.shipmondo);
        Assert.DoesNotContain(ShipmondoServiceCodes.PalletExchange, create.shipmondo!.productServices!);
    }

    // --- "Afhenter Selv" (self-pickup) never gets freight, even if a delivery type lingers ---

    [Fact]
    public void Map_NoShipmondo_ForAfhenterSelv_EvenWhenDeliveryTypeSet()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 4001,
            TransportType = "Afhenter Selv",
            DeliveryType = "GLS" // lingering delivery type must NOT book a carrier for self-pickup
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Null(create.shipmondo);
    }

    [Fact]
    public void Map_NoShipmondo_ForAfhenterSelv_WithoutDeliveryType()
    {
        var so = new LocalSalesOrder { OrderNumber = 4002, TransportType = "Afhenter Selv" };

        var create = new SalesOrderMapper().Map(so);

        Assert.Null(create.shipmondo);
    }

    [Fact]
    public void Map_AddsPalletExchange_ForEgsReroutedPallet_WhenExchangePalletsTrue()
    {
        // DK zip > 4999 reroutes GLIMOE_PARCEL → EGS_STDPL, which also supports PL_EXCHANGE.
        var so = new LocalSalesOrder
        {
            OrderNumber = 3004,
            DeliveryType = "Palle Fragt",
            DeliveryZip = "6700",
            DeliveryCountryCode = "DK",
            ExchangePallets = true
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.NotNull(create.shipmondo);
        Assert.Equal("EGS_STDPL", create.shipmondo!.productCode);
        Assert.Contains(ShipmondoServiceCodes.PalletExchange, create.shipmondo.productServices!);
    }

    // --- Contact person comes from DeliveryContactPerson, not the delivery (location) name ---

    [Fact]
    public void Map_UsesDeliveryContactPerson_NotDeliveryName_ForContactBlock()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 5001,
            DeliveryName = "Dhaba - Tivoli",          // location name — must NOT become the contact person
            DeliveryContactPerson = "Anders Hansen",
            DeliveryContactEmail = "anders@dhaba.dk",
            DeliveryContactPhone = "12345678"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("Anders Hansen", create.contactPerson!.name);
        Assert.NotEqual(so.DeliveryName, create.contactPerson!.name);
        Assert.Equal("anders@dhaba.dk", create.contactPerson!.email);
        Assert.Equal("12345678", create.contactPerson!.telephoneDirect);
        Assert.Equal("12345678", create.contactPerson!.telephoneMobile);
        // Delivery (location) name still drives the address block.
        Assert.Equal("Dhaba - Tivoli", create.address!.name);
    }

    [Fact]
    public void Map_LeavesContactFieldsNull_WhenBlank()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 5002,
            DeliveryName = "Dhaba - Tivoli",
            DeliveryContactPerson = "   ",   // blank → not sent
            DeliveryContactEmail = "",
            DeliveryContactPhone = null
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Null(create.contactPerson!.name);
        Assert.Null(create.contactPerson!.email);
        Assert.Null(create.contactPerson!.telephoneDirect);
        Assert.Null(create.contactPerson!.telephoneMobile);
    }
}

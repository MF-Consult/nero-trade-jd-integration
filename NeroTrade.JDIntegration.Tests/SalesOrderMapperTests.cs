using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the sales-order note routing — each Uniconta note lands in its own JD field:
/// <c>text</c> ("Intern Note") = the xRemarksForJD remark ONLY (blank → null);
/// <c>trackingNote</c> = xTrackingNote (Sporingsnote) with the "SO {n}" machine key appended as
/// "{Sporingsnote} / SO {n}" (bare "SO {n}" when the Sporingsnote is blank), trimmed to fit JD's
/// 30-char trackingNote cap so the key is never truncated;
/// <c>deliveryNoteText</c> ("Note på følgeseddel") = label text only (no UC order number).
/// JdOrderHelper reads the "SO {n}" key back from trackingNote (end-anchored), then text (legacy).
/// </summary>
public class SalesOrderMapperTests
{
    [Fact]
    public void Map_PutsRemarkInText_AndSoKeyInTrackingNote_WhenNoSporingsnote()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2193,
            RemarkText = "Hej Mikkel, kommer min bemærkning med over?"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("Hej Mikkel, kommer min bemærkning med over?", create.text); // remark only, no SO key
        Assert.Equal("SO 2193", create.trackingNote);                             // key on trackingNote
        Assert.Null(create.deliveryNoteText);
    }

    [Fact]
    public void Map_AppendsSoKeyToSporingsnote_AndTextIsRemarkOnly()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2194,
            TrackingNote = "Quattro Fontane Due",
            RemarkText = "  Ring 30 min før  "
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("Ring 30 min før", create.text);                       // Intern Note = remark only
        Assert.Equal("Quattro Fontane Due / SO 2194", create.trackingNote); // Sporingsnote + appended key
        Assert.True(create.trackingNote!.Length <= 30);
        Assert.Null(create.deliveryNoteText);
    }

    [Fact]
    public void Map_WithoutRemark_TextIsNull_AndTrackingNoteCarriesKey()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2195,
            TrackingNote = "Quattro Fontane Due"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Null(create.text);
        Assert.Equal("Quattro Fontane Due / SO 2195", create.trackingNote);
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
        Assert.Equal("intern besked", create.text);                           // remark only
        Assert.Equal("SO 2196", create.trackingNote);                         // key on trackingNote
    }

    [Fact]
    public void Map_TrackingNote_RoundTripsThroughOrderNumberParsing_EvenWhenRemarkMentionsAnotherSo()
    {
        var so = new LocalSalesOrder { OrderNumber = 2197, RemarkText = "vedrører SO 9999" };

        var create = new SalesOrderMapper().Map(so);

        // The key now lives (end-anchored) on trackingNote, so identification is unaffected by the stray
        // "SO 9999" the remark leaves on text. Read priority: trackingNote → text → deliveryNoteText.
        Assert.Equal(2197, JdOrderHelper.GetOrderNumber(
            create.shopOrderId, create.trackingNote, create.text, create.deliveryNoteText));
    }

    [Fact]
    public void Map_TruncatesSporingsnote_ButNeverTheSoKey_WhenOver30Chars()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2198,
            TrackingNote = "Quattro Fontane Due Milano Centrale" // 35 chars — would overflow the 30-char cap
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.True(create.trackingNote!.Length <= 30, $"trackingNote was {create.trackingNote.Length} chars");
        Assert.EndsWith("SO 2198", create.trackingNote); // key survives whole at the tail
        // And it still round-trips: the SO number is recoverable despite the truncation.
        Assert.Equal(2198, JdOrderHelper.GetOrderNumber(
            create.shopOrderId, create.trackingNote, create.text, create.deliveryNoteText));
    }

    [Fact]
    public void GetOrderNumber_FallsBackToText_ForLegacyOrders_WhoseKeyStillLeadsText()
    {
        // In-flight orders created before this change: key led text, trackingNote was the raw Sporingsnote
        // (no appended key). The text fallback must still match them so they are NOT re-sent as "new".
        Assert.Equal(2100, JdOrderHelper.GetOrderNumber(
            shopOrderId: null,
            trackingNote: "Quattro Fontane Due",   // legacy raw Sporingsnote, no "SO {n}"
            text: "SO 2100 - Ring 30 min før",     // legacy: key leads text
            deliveryNoteText: null));
    }

    [Fact]
    public void GetOrderNumber_IgnoresStraySoInsideFreeSporingsnote_ForLegacyOrders()
    {
        // A legacy order whose raw Sporingsnote happens to contain a stray "SO 9999" must NOT be keyed off
        // it — the end-anchored trackingNote regex requires the key at string-start or after our " / "
        // separator, so this falls through to the real key on text.
        Assert.Equal(2101, JdOrderHelper.GetOrderNumber(
            shopOrderId: null,
            trackingNote: "ref til SO 9999",       // stray, mid-string, not our appended form
            text: "SO 2101",
            deliveryNoteText: null));
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

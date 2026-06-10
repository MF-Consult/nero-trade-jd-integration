using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the sales-order note routing (JD UI mapping verified against SO 2193 via the API):
/// the xRemarksForJD remark goes to <c>trackingNote</c> ("Intern note"), while
/// <c>deliveryNoteText</c> ("Note på følgeseddel") keeps the "SO {n}" machine key that
/// JdOrderHelper falls back to when identifying the order on read-back.
/// </summary>
public class SalesOrderMapperTests
{
    [Fact]
    public void Map_PutsRemarkInTrackingNote_AndKeepsSoKeyInDeliveryNoteText()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2193,
            RemarkText = "Hej Mikkel, kommer min bemærkning med over?"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("Hej Mikkel, kommer min bemærkning med over?", create.trackingNote);
        Assert.Equal("SO 2193", create.deliveryNoteText);
    }

    [Fact]
    public void Map_AppendsRemarkAfterExistingTrackingNote()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2194,
            TrackingNote = "Quattro Fontane Due",
            RemarkText = "  Ring 30 min før  "
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("Quattro Fontane Due\nRing 30 min før", create.trackingNote);
        Assert.Equal("SO 2194", create.deliveryNoteText);
    }

    [Fact]
    public void Map_WithoutRemark_LeavesTrackingNoteUntouched_AndNoDanglingSeparator()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2195,
            TrackingNote = "Quattro Fontane Due"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("Quattro Fontane Due", create.trackingNote);
        Assert.Equal("SO 2195", create.deliveryNoteText);
    }

    [Fact]
    public void Map_AppendsDeliveryNoteTextLine_AfterSoKey()
    {
        var so = new LocalSalesOrder
        {
            OrderNumber = 2196,
            DeliveryNoteText = "Label-tekst til følgeseddel",
            RemarkText = "intern besked"
        };

        var create = new SalesOrderMapper().Map(so);

        Assert.Equal("SO 2196\nLabel-tekst til følgeseddel", create.deliveryNoteText);
        Assert.Equal("intern besked", create.trackingNote);
    }

    [Fact]
    public void Map_DeliveryNoteText_RoundTripsThroughOrderNumberParsing()
    {
        var so = new LocalSalesOrder { OrderNumber = 2197, RemarkText = "vedrører SO 9999" };

        var create = new SalesOrderMapper().Map(so);

        // Identification reads shopOrderId → text → deliveryNoteText; the remark (which may itself
        // mention another SO) now lives in trackingNote, which is never parsed.
        Assert.Equal(2197, JdOrderHelper.GetOrderNumber(create.shopOrderId, create.text, create.deliveryNoteText));
    }
}

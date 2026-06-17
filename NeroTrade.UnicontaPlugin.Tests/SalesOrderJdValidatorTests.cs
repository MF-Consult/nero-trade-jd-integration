using Xunit;

namespace NeroTrade.UnicontaPlugin.Tests;

/// <summary>
/// Pins the save-time validation matrix for sales orders flagged for JD transfer.
/// The matrix is documented in NeroTrade.UnicontaPlugin/README.md.
/// Valid cases pass exchangePallets = "Nej" so byttepaller is never the blocker — it is a forced
/// decision only for pallet orders (see the dedicated section below).
/// </summary>
public class SalesOrderJdValidatorTests
{
    private static readonly DateTime SomeDate = new(2026, 6, 15);

    // --- Gate: xTransferToJD == false → never validate ---

    [Fact]
    public void Validate_ReturnsNull_WhenTransferToJdIsFalse_EvenWhenEverythingElseIsMissing()
    {
        var result = SalesOrderJdValidator.Validate(
            transferToJd: false,
            deliveryDate: default,
            trackingNote: null,
            transportType: null,
            deliveryType: null,
            exchangePallets: null);

        Assert.Null(result);
    }

    // --- Valid combinations ---

    [Theory]
    [InlineData("JD Logistik Transport", "GLS")]
    [InlineData("JD Logistik Transport", "Palle Fragt")]
    [InlineData(" jd logistik transport ", " gls ")]
    [InlineData("JD LOGISTIK TRANSPORT", "PALLE FRAGT")]
    public void Validate_ReturnsNull_ForJdLogistikWithDeliveryType(string transport, string delivery)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery, "Nej");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Ekstern Transport", null)]
    [InlineData("Ekstern Transport", "")]
    [InlineData(" ekstern transport ", "   ")]
    [InlineData("Afhenter Selv", null)]
    [InlineData("AFHENTER SELV", "")]
    [InlineData(" afhenter selv ", "   ")]
    public void Validate_ReturnsNull_ForEksternAndAfhenterSelv_WithEmptyDeliveryType(string transport, string? delivery)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery, "Nej");

        Assert.Null(result);
    }

    // --- Rule 1: delivery date ---

    [Fact]
    public void Validate_ReturnsDeliveryDateMessage_WhenDateIsNotSet()
    {
        var result = SalesOrderJdValidator.Validate(true, default, "Sporingsnote", "JD Logistik Transport", "GLS", "Nej");

        Assert.Equal(ValidationMessages.DeliveryDateMissing, result);
    }

    [Fact]
    public void Validate_ChecksDeliveryDateBeforeOtherFields()
    {
        var result = SalesOrderJdValidator.Validate(true, default, null, null, null, null);

        Assert.Equal(ValidationMessages.DeliveryDateMissing, result);
    }

    // --- Rule 2: tracking note ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Validate_ReturnsTrackingNoteMessage_WhenNoteIsEmpty(string? note)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, note, "JD Logistik Transport", "GLS", "Nej");

        Assert.Equal(ValidationMessages.TrackingNoteMissing, result);
    }

    [Fact]
    public void Validate_ChecksTrackingNoteBeforeTransportType()
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, null, null, null, null);

        Assert.Equal(ValidationMessages.TrackingNoteMissing, result);
    }

    // --- Rule 3: transport type ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ReturnsTransportTypeMessage_WhenTransportIsEmpty(string? transport)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, null, "Nej");

        Assert.Equal(ValidationMessages.TransportTypeMissing, result);
    }

    // --- Rule 4a: JD Logistik Transport requires a delivery type ---

    [Theory]
    [InlineData("JD Logistik Transport", null)]
    [InlineData("JD Logistik Transport", "")]
    [InlineData(" jd logistik transport ", "   ")]
    public void Validate_RequiresDeliveryType_ForJdLogistik(string transport, string? delivery)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery, "Nej");

        Assert.Equal(ValidationMessages.DeliveryTypeRequiredForJdLogistik, result);
    }

    // --- Rule 4b: Ekstern Transport / Afhenter Selv must have an EMPTY delivery type ---

    [Theory]
    [InlineData("Ekstern Transport", "GLS", "Ekstern Transport")]
    [InlineData(" ekstern transport ", "Palle Fragt", "Ekstern Transport")]
    [InlineData("Afhenter Selv", "GLS", "Afhenter Selv")]
    [InlineData("AFHENTER SELV", "Palle Fragt", "Afhenter Selv")]
    public void Validate_RejectsDeliveryType_ForEksternAndAfhenterSelv(
        string transport, string delivery, string expectedTransportInMessage)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery, "Nej");

        Assert.Equal(
            string.Format(ValidationMessages.DeliveryTypeMustBeEmptyFormat, expectedTransportInMessage),
            result);
    }

    // --- Unknown transport types pass through (must never block saves) ---

    [Theory]
    [InlineData("Noget Helt Nyt", null)]
    [InlineData("Noget Helt Nyt", "GLS")]
    public void Validate_ReturnsNull_ForUnknownTransportType_RegardlessOfDeliveryType(
        string transport, string? delivery)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery, "Nej");

        Assert.Null(result);
    }

    // --- Rule 5: byttepaller is a forced decision, but ONLY for pallet orders ---

    [Theory]
    [InlineData("JD Logistik Transport", "Palle Fragt", null)] // pallet via delivery type
    [InlineData("JD Logistik Transport", "Palle Fragt", "")]
    [InlineData("JD Logistik Transport", "Palle Fragt", "   ")]
    [InlineData("Ekstern Transport", null, null)]              // pallet via Ekstern + empty delivery
    [InlineData("Ekstern Transport", "", "   ")]
    public void Validate_ReturnsExchangePalletsMessage_ForPalletOrder_WhenNotChosen(
        string transport, string? delivery, string? exchangePallets)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery, exchangePallets);

        Assert.Equal(ValidationMessages.ExchangePalletsRequired, result);
    }

    [Theory]
    [InlineData("JD Logistik Transport", "GLS", null)]   // parcel → byttepaller not required
    [InlineData("JD Logistik Transport", "GLS", "")]
    [InlineData("Afhenter Selv", null, null)]            // pickup → byttepaller not required
    [InlineData("Afhenter Selv", "", "   ")]
    public void Validate_ReturnsNull_ForNonPalletOrder_EvenWhenExchangePalletsBlank(
        string transport, string? delivery, string? exchangePallets)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery, exchangePallets);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Ja")]
    [InlineData("Nej")]
    public void Validate_ReturnsNull_ForPalletOrder_WhenExchangePalletsChosen(string exchangePallets)
    {
        var result = SalesOrderJdValidator.Validate(
            true, SomeDate, "Sporingsnote", "JD Logistik Transport", "Palle Fragt", exchangePallets);

        Assert.Null(result);
    }

    [Fact]
    public void Validate_ChecksTransportAndDeliveryBeforeExchangePallets()
    {
        // Delivery-type error must surface before the byttepaller check, even with byttepaller blank.
        var result = SalesOrderJdValidator.Validate(
            true, SomeDate, "Sporingsnote", "JD Logistik Transport", deliveryType: null, exchangePallets: null);

        Assert.Equal(ValidationMessages.DeliveryTypeRequiredForJdLogistik, result);
    }

    [Fact]
    public void Validate_ReturnsNull_WhenExchangePalletsBlank_ButTransferFlagOff()
    {
        var result = SalesOrderJdValidator.Validate(
            false, SomeDate, "Sporingsnote", "JD Logistik Transport", "GLS", exchangePallets: null);

        Assert.Null(result);
    }
}

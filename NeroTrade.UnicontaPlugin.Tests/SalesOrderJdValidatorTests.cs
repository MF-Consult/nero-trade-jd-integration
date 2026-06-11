using Xunit;

namespace NeroTrade.UnicontaPlugin.Tests;

/// <summary>
/// Pins the save-time validation matrix for sales orders flagged for JD transfer.
/// The matrix is documented in NeroTrade.UnicontaPlugin/README.md.
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
            deliveryType: null);

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
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery);

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
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery);

        Assert.Null(result);
    }

    // --- Rule 1: delivery date ---

    [Fact]
    public void Validate_ReturnsDeliveryDateMessage_WhenDateIsNotSet()
    {
        var result = SalesOrderJdValidator.Validate(true, default, "Sporingsnote", "JD Logistik Transport", "GLS");

        Assert.Equal(ValidationMessages.DeliveryDateMissing, result);
    }

    [Fact]
    public void Validate_ChecksDeliveryDateBeforeOtherFields()
    {
        var result = SalesOrderJdValidator.Validate(true, default, null, null, null);

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
        var result = SalesOrderJdValidator.Validate(true, SomeDate, note, "JD Logistik Transport", "GLS");

        Assert.Equal(ValidationMessages.TrackingNoteMissing, result);
    }

    [Fact]
    public void Validate_ChecksTrackingNoteBeforeTransportType()
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, null, null, null);

        Assert.Equal(ValidationMessages.TrackingNoteMissing, result);
    }

    // --- Rule 3: transport type ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ReturnsTransportTypeMessage_WhenTransportIsEmpty(string? transport)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, null);

        Assert.Equal(ValidationMessages.TransportTypeMissing, result);
    }

    // --- Rule 4a: JD Logistik Transport requires a delivery type ---

    [Theory]
    [InlineData("JD Logistik Transport", null)]
    [InlineData("JD Logistik Transport", "")]
    [InlineData(" jd logistik transport ", "   ")]
    public void Validate_RequiresDeliveryType_ForJdLogistik(string transport, string? delivery)
    {
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery);

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
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery);

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
        var result = SalesOrderJdValidator.Validate(true, SomeDate, "Sporingsnote", transport, delivery);

        Assert.Null(result);
    }
}

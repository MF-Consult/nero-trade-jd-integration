using Xunit;

namespace NeroTrade.UnicontaPlugin.Tests;

/// <summary>
/// Pins the transport-type driven behaviour of xDeliveryType: default value on "JD Logistik
/// Transport", clearing on the others, and the show/hide decision. The plugin (UI side) calls
/// these same rules; <see cref="SalesOrderJdValidatorTests"/> covers the save-time backstop.
/// </summary>
public class DeliveryTypeRulesTests
{
    // --- ShouldShowDeliveryType: only JD Logistik shows the field ---

    [Theory]
    [InlineData("JD Logistik Transport")]
    [InlineData(" jd logistik transport ")]
    [InlineData("JD LOGISTIK TRANSPORT")]
    public void ShouldShowDeliveryType_ReturnsTrue_ForJdLogistik(string transport)
        => Assert.True(DeliveryTypeRules.ShouldShowDeliveryType(transport));

    [Theory]
    [InlineData("Ekstern Transport")]
    [InlineData("Afhenter Selv")]
    [InlineData("Noget Helt Nyt")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShouldShowDeliveryType_ReturnsFalse_ForEverythingElse(string? transport)
        => Assert.False(DeliveryTypeRules.ShouldShowDeliveryType(transport));

    // --- JD Logistik: default to Palle Fragt only when empty, keep an existing choice ---

    [Theory]
    [InlineData("JD Logistik Transport", null)]
    [InlineData("JD Logistik Transport", "")]
    [InlineData(" jd logistik transport ", "   ")]
    public void Resolve_DefaultsToPalleFragt_ForJdLogistik_WhenEmpty(string transport, string? current)
        => Assert.Equal(DeliveryTypeValues.PalleFragt,
            DeliveryTypeRules.ResolveDeliveryTypeOnTransportChange(transport, current));

    [Theory]
    [InlineData("GLS")]
    [InlineData("Palle Fragt")]
    public void Resolve_KeepsExistingChoice_ForJdLogistik_WhenAlreadySet(string current)
        => Assert.Null(DeliveryTypeRules.ResolveDeliveryTypeOnTransportChange("JD Logistik Transport", current));

    // --- Ekstern / Afhenter Selv: clear a filled value, no-op when already empty ---

    [Theory]
    [InlineData("Ekstern Transport", "GLS")]
    [InlineData(" ekstern transport ", "Palle Fragt")]
    [InlineData("Afhenter Selv", "GLS")]
    [InlineData("AFHENTER SELV", "Palle Fragt")]
    public void Resolve_ClearsDeliveryType_ForEksternAndAfhenterSelv_WhenSet(string transport, string current)
        => Assert.Equal("", DeliveryTypeRules.ResolveDeliveryTypeOnTransportChange(transport, current));

    [Theory]
    [InlineData("Ekstern Transport", null)]
    [InlineData("Ekstern Transport", "")]
    [InlineData("Afhenter Selv", "   ")]
    public void Resolve_ReturnsNull_ForEksternAndAfhenterSelv_WhenAlreadyEmpty(string transport, string? current)
        => Assert.Null(DeliveryTypeRules.ResolveDeliveryTypeOnTransportChange(transport, current));

    // --- Unknown / empty transport: never touch xDeliveryType ---

    [Theory]
    [InlineData("Noget Helt Nyt", "GLS")]
    [InlineData("Noget Helt Nyt", null)]
    [InlineData(null, "GLS")]
    [InlineData("", "GLS")]
    public void Resolve_ReturnsNull_ForUnknownOrEmptyTransport(string? transport, string? current)
        => Assert.Null(DeliveryTypeRules.ResolveDeliveryTypeOnTransportChange(transport, current));
}

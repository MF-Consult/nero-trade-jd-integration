using Xunit;

namespace NeroTrade.UnicontaPlugin.Tests;

/// <summary>
/// Pins when byttepaller (xByttepaller) applies: only pallet orders — exactly the cases
/// SalesOrderMapper can send PL_EXCHANGE for. Drives both the forced-decision validation and the
/// show/hide of the field.
/// </summary>
public class ExchangePalletsRulesTests
{
    // --- Relevant (pallet order) ---

    [Theory]
    [InlineData("JD Logistik Transport", "Palle Fragt")]
    [InlineData(" jd logistik transport ", " palle fragt ")]
    [InlineData(null, "Palle Fragt")]              // "Palle Fragt" → pallet regardless of transport
    [InlineData("Noget Helt Nyt", "Palle Fragt")]
    [InlineData("Ekstern Transport", null)]        // empty delivery + Ekstern Transport → pallet
    [InlineData("Ekstern Transport", "")]
    [InlineData(" ekstern transport ", "   ")]
    public void IsRelevant_ReturnsTrue_ForPalletOrders(string? transport, string? delivery)
        => Assert.True(ExchangePalletsRules.IsRelevant(transport, delivery));

    // --- Not relevant (parcel / pickup / unknown) ---

    [Theory]
    [InlineData("JD Logistik Transport", "GLS")]   // parcel
    [InlineData("Afhenter Selv", null)]            // pickup, no delivery type
    [InlineData("Afhenter Selv", "")]
    [InlineData("JD Logistik Transport", null)]    // empty delivery + non-Ekstern → not pallet
    [InlineData("Noget Helt Nyt", null)]           // unknown transport, no delivery type
    [InlineData("Noget Helt Nyt", "GLS")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void IsRelevant_ReturnsFalse_ForNonPalletOrders(string? transport, string? delivery)
        => Assert.False(ExchangePalletsRules.IsRelevant(transport, delivery));
}

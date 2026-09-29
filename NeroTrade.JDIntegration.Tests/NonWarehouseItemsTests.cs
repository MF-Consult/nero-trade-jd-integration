using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the purchase-line exclusion list agreed with Nero Trade on 2026-09-29. Regression for PO 26, which JD
/// rejected because it carried the cost items PRINTPLA and Forud ("No matching JD catalog item").
/// </summary>
public class NonWarehouseItemsTests
{
    [Theory]
    [InlineData("PRINTPLA", null)]
    [InlineData("printpla", null)]
    [InlineData(" Forud ", null)]
    [InlineData("TRP-01", "Cargo - Transport")]
    [InlineData("TRP-02", "cargo transport internal")]
    [InlineData("Cargo - Transport", null)]
    public void IsExcluded_MatchesItemNumberOrName(string? itemNumber, string? itemName)
    {
        Assert.True(NonWarehouseItems.IsExcluded(itemNumber, itemName));
    }

    [Fact]
    public void IsExcluded_MatchesLineTextWhenItemNameIsUnavailable()
    {
        // Without the SDK's client-side item cache the item-name getter returns null; the line text,
        // which Uniconta pre-fills with the item name, carries the name instead.
        Assert.True(NonWarehouseItems.IsExcluded("TRP-01", "Cargo - Transport", null));
    }

    [Theory]
    [InlineData("TRBFTELIPWW610", "Some product")]
    [InlineData("FORUD2", null)]           // exact match only — no prefix/substring hits
    [InlineData("Cargo", "Cargo - Transportkasse")]
    [InlineData(null, null)]
    public void IsExcluded_LeavesRealItemsAlone(string? itemNumber, string? itemName)
    {
        Assert.False(NonWarehouseItems.IsExcluded(itemNumber, itemName));
    }
}

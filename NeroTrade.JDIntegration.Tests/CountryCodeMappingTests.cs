using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;
using Uniconta.Common;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the Uniconta → JD country translation. JD requires an ISO 3166-1 alpha-2 code; Uniconta's
/// <see cref="CountryCode"/> enum stringifies to a country <i>name</i>. Regression for 2026-09-28: item
/// TRBFTELIPWW610 (Oprindelsesland = Türkiye) reached JD as producedInCountryCode "TÜRKIYE" and was rejected.
/// </summary>
public class CountryCodeMappingTests
{
    [Theory]
    [InlineData(CountryCode.Türkiye, "TR")]
    [InlineData(CountryCode.Denmark, "DK")]
    [InlineData(CountryCode.UnitedKingdom, "GB")]
    [InlineData(CountryCode.UnitedStates, "US")]
    [InlineData(CountryCode.Vietnam, "VN")]
    [InlineData(CountryCode.Guadeloupe, "GP")]
    public void ToIsoCode_MapsEnumToIsoAlpha2(CountryCode country, string expected)
    {
        Assert.Equal(expected, UnicontaCountry.ToIsoCode(country));
    }

    [Fact]
    public void ToIsoCode_UnknownOrNull_IsNull()
    {
        Assert.Null(UnicontaCountry.ToIsoCode(null));
        Assert.Null(UnicontaCountry.ToIsoCode(CountryCode.Unknown));
    }

    [Fact]
    public void ToIsoCode_EveryDefinedCountry_YieldsTwoUppercaseLetters()
    {
        foreach (var country in Enum.GetValues<CountryCode>().Where(c => c != CountryCode.Unknown))
        {
            var iso = UnicontaCountry.ToIsoCode(country);
            Assert.True(iso is { Length: 2 } && iso.All(char.IsAsciiLetterUpper), $"{country} -> '{iso}'");
        }
    }

    [Theory]
    [InlineData("Türkiye")]
    [InlineData("UnitedKingdom")]
    [InlineData("Atlantis")]
    public void CountryHelper_NeverReturnsANameAsTheCode(string name)
    {
        var (code, _) = CountryHelper.GetCountryInfo(name);
        Assert.True(code is null || code.Length == 2, $"'{name}' -> code '{code}'");
    }

    [Fact]
    public void CountryHelper_PassesThroughIsoCodesOutsideItsTable()
    {
        Assert.Equal(("TR", null), CountryHelper.GetCountryInfo("tr"));
        Assert.Equal(("DK", "Denmark"), CountryHelper.GetCountryInfo("DK"));
    }

    [Fact]
    public void ItemMapper_SendsIsoOriginCode()
    {
        var item = new LocalInventoryItem
        {
            Sku = "TRBFTELIPWW610",
            ProducedInCountryCode = UnicontaCountry.ToIsoCode(CountryCode.Türkiye)
        };

        Assert.Equal("TR", new ItemMapper().Map(item).producedInCountryCode);
    }

    [Fact]
    public void SalesOrderMapper_UsesIsoCodeAndFallsBackToItAsCountryName()
    {
        var so = new LocalSalesOrder { OrderNumber = 1, DeliveryCountryCode = "TR" };

        var address = new SalesOrderMapper().Map(so).address!;

        Assert.Equal("TR", address.countryCode);
        Assert.Equal("TR", address.country);
    }
}

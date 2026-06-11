using Xunit;

namespace NeroTrade.UnicontaPlugin.Tests;

/// <summary>
/// Pins the enum user-field normalization: GetUserField may return the value string
/// or an integer index depending on Uniconta version; both must resolve to the
/// canonical string values the validation matrix is defined on.
/// </summary>
public class UserFieldValueNormalizerTests
{
    [Fact]
    public void Normalize_ReturnsNull_ForNull()
    {
        Assert.Null(UserFieldValueNormalizer.Normalize(null, TransportTypeValues.InIndexOrder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsNull_ForBlankStrings(string raw)
    {
        Assert.Null(UserFieldValueNormalizer.Normalize(raw, TransportTypeValues.InIndexOrder));
    }

    [Theory]
    [InlineData(0, "JD Logistik Transport")]
    [InlineData(1, "Ekstern Transport")]
    [InlineData(2, "Afhenter Selv")]
    public void Normalize_MapsIntIndex_ToTransportTypeValue(int index, string expected)
    {
        Assert.Equal(expected, UserFieldValueNormalizer.Normalize(index, TransportTypeValues.InIndexOrder));
    }

    [Theory]
    [InlineData(0, "GLS")]
    [InlineData(1, "Palle Fragt")]
    public void Normalize_MapsIntIndex_ToDeliveryTypeValue(int index, string expected)
    {
        Assert.Equal(expected, UserFieldValueNormalizer.Normalize(index, DeliveryTypeValues.InIndexOrder));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void Normalize_ReturnsNull_ForOutOfRangeIndex(int index)
    {
        Assert.Null(UserFieldValueNormalizer.Normalize(index, TransportTypeValues.InIndexOrder));
    }

    [Theory]
    [InlineData("0", "JD Logistik Transport")]
    [InlineData(" 1 ", "Ekstern Transport")]
    [InlineData("2", "Afhenter Selv")]
    public void Normalize_MapsNumericString_ToValue(string raw, string expected)
    {
        Assert.Equal(expected, UserFieldValueNormalizer.Normalize(raw, TransportTypeValues.InIndexOrder));
    }

    [Theory]
    [InlineData("jd logistik transport", "JD Logistik Transport")]
    [InlineData(" EKSTERN TRANSPORT ", "Ekstern Transport")]
    [InlineData(" gls ", "GLS")]
    public void Normalize_MatchesCaseInsensitive_AndReturnsCanonicalCasing(string raw, string expected)
    {
        var values = expected == "GLS" ? DeliveryTypeValues.InIndexOrder : TransportTypeValues.InIndexOrder;

        Assert.Equal(expected, UserFieldValueNormalizer.Normalize(raw, values));
    }

    [Fact]
    public void Normalize_PassesUnknownStringsThrough_Trimmed()
    {
        Assert.Equal("Noget Andet", UserFieldValueNormalizer.Normalize("  Noget Andet ", TransportTypeValues.InIndexOrder));
    }

    [Fact]
    public void Normalize_MapsByteShortAndLongIndexes()
    {
        Assert.Equal("JD Logistik Transport", UserFieldValueNormalizer.Normalize((byte)0, TransportTypeValues.InIndexOrder));
        Assert.Equal("Ekstern Transport", UserFieldValueNormalizer.Normalize((short)1, TransportTypeValues.InIndexOrder));
        Assert.Equal("Afhenter Selv", UserFieldValueNormalizer.Normalize(2L, TransportTypeValues.InIndexOrder));
    }

    [Fact]
    public void Normalize_ReturnsNull_ForLongOutsideIntRange()
    {
        Assert.Null(UserFieldValueNormalizer.Normalize((long)int.MaxValue + 1, TransportTypeValues.InIndexOrder));
    }
}

using NeroTrade.JDIntegration.Models.ExternalIntegration;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the delivery-date normalization sent to JD. The old logic overwrote a same-day "deliver
/// today" order with the current UTC instant, which JD pushed to the next business day. The fix
/// keeps the intended calendar day and only bumps a genuinely past day up to today.
/// </summary>
public class JdDeliveryDateTests
{
    private static readonly DateTime NowUtc = new(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc); // Friday

    [Fact]
    public void Normalize_ReturnsNull_WhenDateIsNull()
        => Assert.Null(JdDeliveryDate.Normalize(null, NowUtc));

    [Fact]
    public void Normalize_KeepsToday_ForSameDayDelivery_AtNoon()
    {
        // "Deliver today" (midnight) must stay today — previously it was bumped to "now" and shifted.
        var result = JdDeliveryDate.Normalize(new DateTime(2026, 6, 12, 0, 0, 0), NowUtc);

        Assert.Equal(new DateTime(2026, 6, 12, 12, 0, 0), result);
    }

    [Fact]
    public void Normalize_BumpsPastDay_UpToToday_AtNoon()
    {
        var result = JdDeliveryDate.Normalize(new DateTime(2026, 6, 11, 0, 0, 0), NowUtc);

        Assert.Equal(new DateTime(2026, 6, 12, 12, 0, 0), result);
    }

    [Fact]
    public void Normalize_KeepsFutureDay_AtNoon()
    {
        var result = JdDeliveryDate.Normalize(new DateTime(2026, 6, 15, 0, 0, 0), NowUtc);

        Assert.Equal(new DateTime(2026, 6, 15, 12, 0, 0), result);
    }

    [Fact]
    public void Normalize_PreservesExplicitTime_ForTimedDelivery()
    {
        var result = JdDeliveryDate.Normalize(new DateTime(2026, 6, 12, 14, 30, 0), NowUtc);

        Assert.Equal(new DateTime(2026, 6, 12, 14, 30, 0), result);
    }

    [Fact]
    public void Normalize_BumpsPastDay_ButKeepsExplicitTime()
    {
        var result = JdDeliveryDate.Normalize(new DateTime(2026, 6, 10, 9, 15, 0), NowUtc);

        Assert.Equal(new DateTime(2026, 6, 12, 9, 15, 0), result);
    }
}

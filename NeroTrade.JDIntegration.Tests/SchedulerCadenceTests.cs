using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Scheduling;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the "configure just below the multiple of 30 s" rule.
///
/// `SyncDispatcher` ticks every 30 s and `TryBeginRun` stamps the last run a few hundred milliseconds
/// after the tick fires. A job configured at exactly 60 s therefore measures ~59.9 s at the 60 s tick,
/// fails the `>=` test, and slips to the next one. Setting SalesOrders to 60 was measured in production
/// on 2026-07-29 as a 60/90 alternation averaging 75 s — a 25% cadence loss from a value that looks right.
/// </summary>
public class SchedulerCadenceTests
{
    // What a real tick looks like: the heartbeat fires on the 30 s boundary, the scheduler stamps a moment
    // later, so the elapsed time measured at the next boundary is just under the round number.
    private static readonly TimeSpan TickJitter = TimeSpan.FromMilliseconds(400);

    [Theory]
    [InlineData(50)]  // SalesOrders
    [InlineData(72)]  // PurchaseOrders, PostedPurchaseInvoices
    public void ConfiguredJustBelowTheBoundary_IsDueOnTheIntendedTick(int configuredSeconds)
    {
        var interval = TimeSpan.FromSeconds(configuredSeconds);
        var intendedTick = 30 * (int)Math.Ceiling(configuredSeconds / 30.0);
        var lastRun = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc) + TickJitter;

        var atIntendedTick = lastRun - TickJitter + TimeSpan.FromSeconds(intendedTick);

        Assert.True(SyncScheduler.IsDue(interval, atIntendedTick, lastRun),
            $"{configuredSeconds}s should be due at the {intendedTick}s tick");
    }

    [Fact]
    public void ConfiguredExactlyOnTheBoundary_MissesItsTickAndSlips()
    {
        // The regression this rule exists to prevent: 60 s does NOT fire at the 60 s tick.
        var lastRun = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc) + TickJitter;
        var at60 = lastRun - TickJitter + TimeSpan.FromSeconds(60);
        var at90 = lastRun - TickJitter + TimeSpan.FromSeconds(90);

        Assert.False(SyncScheduler.IsDue(TimeSpan.FromSeconds(60), at60, lastRun));
        Assert.True(SyncScheduler.IsDue(TimeSpan.FromSeconds(60), at90, lastRun));
    }

    [Fact]
    public void ShippedCadences_AllSitJustBelowATickBoundary()
    {
        // Guards the whole config at once: every day interval must leave room for the stamp jitter.
        foreach (var (job, cadence) in new SyncSchedulingOptions().Jobs)
        {
            var remainder = cadence.DaySeconds % 30;
            Assert.True(remainder is > 0 and <= 29,
                $"{job}: DaySeconds={cadence.DaySeconds} lands exactly on a 30 s tick boundary and will slip a tick");
        }
    }
}

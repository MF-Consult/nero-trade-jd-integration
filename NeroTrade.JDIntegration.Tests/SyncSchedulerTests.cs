using Microsoft.Extensions.Logging.Abstractions;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Scheduling;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the day/night scheduling that keeps Uniconta call volume under budget. The sync functions
/// fire a fast heartbeat and defer the real cadence to <see cref="SyncScheduler"/>, so this is where
/// the "is this tick due?" and "which session age applies?" decisions are locked down. Time-zone tests
/// use deterministic zones (UTC, fixed-offset Etc/GMT-2) so they behave identically on Windows dev
/// boxes and the Linux CI runner regardless of DST.
/// </summary>
public class SyncSchedulerTests
{
    private static SyncScheduler Build(SyncSchedulingOptions options)
        => new(options, NullLogger<SyncScheduler>.Instance);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    [Fact]
    public void IsDue_False_WhenElapsedBelowInterval()
    {
        var last = Utc(2026, 7, 10, 10, 0, 0);
        Assert.False(SyncScheduler.IsDue(TimeSpan.FromSeconds(50), last.AddSeconds(40), last));
    }

    [Fact]
    public void IsDue_True_WhenElapsedAtOrAboveInterval()
    {
        var last = Utc(2026, 7, 10, 10, 0, 0);
        Assert.True(SyncScheduler.IsDue(TimeSpan.FromSeconds(50), last.AddSeconds(50), last));
        Assert.True(SyncScheduler.IsDue(TimeSpan.FromSeconds(50), last.AddSeconds(90), last));
    }

    [Fact]
    public void IsDay_UsesWindow_InUtc()
    {
        var s = Build(new SyncSchedulingOptions { TimeZoneId = "UTC", DayStartHour = 7, DayEndHour = 22 });

        Assert.True(s.IsDay(Utc(2026, 7, 10, 7)));    // start hour inclusive
        Assert.True(s.IsDay(Utc(2026, 7, 10, 8)));
        Assert.False(s.IsDay(Utc(2026, 7, 10, 22)));  // end hour exclusive
        Assert.False(s.IsDay(Utc(2026, 7, 10, 23)));
        Assert.False(s.IsDay(Utc(2026, 7, 10, 2)));
    }

    [Fact]
    public void IsDay_AppliesConfiguredTimeZoneOffset()
    {
        // Etc/GMT-2 is a fixed UTC+2 zone (no DST) available on both Windows and Linux. It proves the
        // scheduler converts to local time before checking the window rather than reading UTC directly.
        var s = Build(new SyncSchedulingOptions { TimeZoneId = "Etc/GMT-2", DayStartHour = 7, DayEndHour = 22 });

        Assert.True(s.IsDay(Utc(2026, 7, 10, 6, 0)));    // 06:00 UTC -> 08:00 local -> day
        Assert.False(s.IsDay(Utc(2026, 7, 10, 20, 30))); // 20:30 UTC -> 22:30 local -> night
    }

    [Fact]
    public void TryBeginRun_EnforcesDayInterval()
    {
        var s = Build(new SyncSchedulingOptions
        {
            TimeZoneId = "UTC",
            Jobs = new() { ["SalesOrders"] = new SyncSchedulingOptions.JobCadence { DaySeconds = 50, NightSeconds = 300 } }
        });
        var t = Utc(2026, 7, 10, 10, 0, 0); // daytime

        Assert.True(s.TryBeginRun("SalesOrders", t));            // first run always due
        Assert.False(s.TryBeginRun("SalesOrders", t.AddSeconds(40)));
        Assert.True(s.TryBeginRun("SalesOrders", t.AddSeconds(50)));
    }

    [Fact]
    public void TryBeginRun_UsesNightInterval_Overnight()
    {
        var s = Build(new SyncSchedulingOptions
        {
            TimeZoneId = "UTC",
            Jobs = new() { ["SalesOrders"] = new SyncSchedulingOptions.JobCadence { DaySeconds = 50, NightSeconds = 300 } }
        });
        var t = Utc(2026, 7, 10, 2, 0, 0); // night

        Assert.True(s.TryBeginRun("SalesOrders", t));
        Assert.False(s.TryBeginRun("SalesOrders", t.AddSeconds(200))); // below night interval, still night
        Assert.True(s.TryBeginRun("SalesOrders", t.AddSeconds(300)));
    }

    [Fact]
    public void TryBeginRun_UnknownJob_RunsEveryTick()
    {
        var s = Build(new SyncSchedulingOptions { TimeZoneId = "UTC", Jobs = new() });
        var t = Utc(2026, 7, 10, 10, 0, 0);

        Assert.True(s.TryBeginRun("NotConfigured", t));
        Assert.True(s.TryBeginRun("NotConfigured", t)); // no interval -> always due, fails open
    }

    [Fact]
    public void GetSessionMaxAge_SwitchesDayNight()
    {
        var s = Build(new SyncSchedulingOptions
        {
            TimeZoneId = "UTC",
            DayStartHour = 7,
            DayEndHour = 22,
            SessionMaxAgeDaySeconds = 90,
            SessionMaxAgeNightSeconds = 900
        });

        Assert.Equal(TimeSpan.FromSeconds(90), s.GetSessionMaxAge(Utc(2026, 7, 10, 10)));
        Assert.Equal(TimeSpan.FromSeconds(900), s.GetSessionMaxAge(Utc(2026, 7, 10, 2)));
    }

    [Fact]
    public void UnknownTimeZone_FallsBackToUtc_WithoutThrowing()
    {
        var s = Build(new SyncSchedulingOptions { TimeZoneId = "Totally/Bogus", DayStartHour = 7, DayEndHour = 22 });

        // Falls back to UTC, so the window is evaluated against the UTC hour directly.
        Assert.True(s.IsDay(Utc(2026, 7, 10, 10)));
        Assert.False(s.IsDay(Utc(2026, 7, 10, 23)));
    }
}

namespace NeroTrade.JDIntegration.Models.Settings;

/// <summary>
/// Day/night cadence for the timer-triggered sync jobs, plus the session-age policy. This is the
/// single source of truth for how often each sync actually hits Uniconta — the functions' own
/// <c>[TimerTrigger(...)]</c> cron is only a fast heartbeat, and <see cref="Services.Scheduling.SyncScheduler"/>
/// decides per tick whether a job is due (returning before any Uniconta call otherwise).
///
/// NCRONTAB cannot express sub-minute-but-not-dividing-60 intervals (50s, 72s) nor a day/night
/// switch, which is why the frequency lives here in config rather than in the cron string.
///
/// The defaults encode the agreed budget target of &lt; 4.000 Uniconta calls/day (single instance):
/// fast during the local day window, throttled at night, with the Uniconta session allowed to live
/// much longer at night since no one edits in the Uniconta UI then (so the daytime staleness concern
/// that forces a short <see cref="SessionMaxAgeDaySeconds"/> does not apply).
/// </summary>
public sealed class SyncSchedulingOptions
{
    public const string SectionName = "SyncScheduling";

    /// <summary>
    /// Windows or IANA time-zone id used to decide the local day/night window. .NET 8 resolves both
    /// id styles cross-platform; <see cref="Services.Scheduling.SyncScheduler"/> falls back to UTC
    /// with a warning if the id is unknown on the host.
    /// </summary>
    public string TimeZoneId { get; init; } = "Romance Standard Time";

    /// <summary>Local hour (inclusive) at which the fast day cadence starts.</summary>
    public int DayStartHour { get; init; } = 7;

    /// <summary>Local hour (exclusive) at which the day cadence ends and the night cadence begins.</summary>
    public int DayEndHour { get; init; } = 22;

    /// <summary>
    /// Max Uniconta session age during the day window. Short on purpose — see UnicontaConnectionManager.
    ///
    /// Raised 90 → 150 s on 2026-07-28 to buy back call budget: each recycle costs a Login + an
    /// OpenCompany, and measurement showed those handshakes were ~56% of all Uniconta calls (~2.900 of
    /// ~5.200/day). The cost is freshness: a Uniconta UI edit can now go unseen for up to 150 s instead of
    /// 90 s, plus the job's own cadence. Do not raise it much further without evidence — the short age is
    /// the mitigation for the per-session server-side staleness seen in "ordre 2161" (2026-05-27), which is
    /// a different problem from the SetCache one fixed on 2026-07-27 and is NOT addressed by filtered reads.
    /// </summary>
    public int SessionMaxAgeDaySeconds { get; init; } = 150;

    /// <summary>Max Uniconta session age at night. Long is safe: no UI edits at night, so no staleness risk.</summary>
    public int SessionMaxAgeNightSeconds { get; init; } = 900;

    /// <summary>
    /// Per-job day/night interval, keyed by job name. Keys must match the strings passed to
    /// <see cref="Services.Scheduling.SyncScheduler.TryBeginRun"/> from each sync function.
    /// </summary>
    public Dictionary<string, JobCadence> Jobs { get; init; } = new()
    {
        ["SalesOrders"] = new JobCadence { DaySeconds = 50, NightSeconds = 300 },
        ["PurchaseOrders"] = new JobCadence { DaySeconds = 72, NightSeconds = 1800 },
        // Same day cadence as PurchaseOrders on purpose: a booked-before-flagged purchase order is caught
        // only by this safety-net, so it should not wait five times longer than the normal path. With the
        // 30 s heartbeat both land on an effective ~90 s. Costs ~434 extra Uniconta calls/day (one read per
        // run); the session-age change above pays for it several times over.
        ["PostedPurchaseInvoices"] = new JobCadence { DaySeconds = 72, NightSeconds = 1800 },
        ["RequestOrderStatus"] = new JobCadence { DaySeconds = 300, NightSeconds = 1800 },
        ["Items"] = new JobCadence { DaySeconds = 180, NightSeconds = 3600 },
        ["ReceivedQuantity"] = new JobCadence { DaySeconds = 900, NightSeconds = 3600 },
    };

    public sealed class JobCadence
    {
        public int DaySeconds { get; init; }
        public int NightSeconds { get; init; }
    }
}

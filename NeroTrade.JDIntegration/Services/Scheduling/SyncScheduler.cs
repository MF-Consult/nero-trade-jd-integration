using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.Settings;
using System.Collections.Concurrent;

namespace NeroTrade.JDIntegration.Services.Scheduling;

/// <summary>
/// Decides, per tick, whether a sync job is due to run and what session age to allow, based on the
/// configured day/night cadence in <see cref="SyncSchedulingOptions"/>.
///
/// The sync functions fire a fast heartbeat timer and call <see cref="TryBeginRun"/> first thing;
/// on a non-due tick they return before touching Uniconta, so the heartbeat is free and the effective
/// cadence is driven entirely by config. Last-run timestamps are held in memory, so this assumes the
/// Function app runs on a single instance (see docs/operations.md); a scale-out would give each
/// instance its own schedule and multiply the call count.
/// </summary>
public sealed class SyncScheduler
{
    private readonly SyncSchedulingOptions _options;
    private readonly ILogger<SyncScheduler> _logger;
    private readonly TimeZoneInfo _timeZone;
    private readonly ConcurrentDictionary<string, DateTime> _lastRunUtc = new();
    private readonly ConcurrentDictionary<string, byte> _runNow = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastWebhookUtc = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastFullSweepUtc = new();

    /// <summary>
    /// Minimum spacing between two event-driven runs of the same job. A burst of webhooks (a whole batch of
    /// orders dispatched at once) then costs one run, not one per event, which keeps the Uniconta call budget bounded.
    /// </summary>
    public static readonly TimeSpan RunNowMinGap = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Asks for the job to run on the next dispatcher tick (≤30 s) regardless of its configured cadence — used by
    /// the JD webhook endpoint. The request is consumed by the next <see cref="TryBeginRun"/>; it is subject to
    /// <see cref="RunNowMinGap"/> and to the normal in-memory, single-instance assumption.
    /// </summary>
    public void RequestRunNow(string job) => _runNow[job] = 1;

    /// <summary>Notes that a JD webhook for <paramref name="job"/> arrived. Used to tell whether a later poll finding was announced.</summary>
    public void RecordWebhook(string job, DateTime nowUtc) => _lastWebhookUtc[job] = nowUtc;

    /// <summary>When the last webhook for the job arrived (this process lifetime), or null.</summary>
    public DateTime? LastWebhookUtc(string job) => _lastWebhookUtc.TryGetValue(job, out var t) ? t : null;

    /// <summary>
    /// True when the job's expensive "full" variant is due (never run in this process, or <paramref name="every"/> has
    /// elapsed). Call <see cref="MarkFullSweep"/> once the full variant has succeeded.
    /// </summary>
    public bool IsFullSweepDue(string job, DateTime nowUtc, TimeSpan every)
        => !_lastFullSweepUtc.TryGetValue(job, out var last) || nowUtc - last >= every;

    public void MarkFullSweep(string job, DateTime nowUtc) => _lastFullSweepUtc[job] = nowUtc;

    /// <summary>
    /// A job is stale when it has not started for much longer than its cadence — the in-process heartbeat for
    /// "the dispatcher stopped calling it" or "a run is wedged". 2.5x the current interval plus a minute of slack.
    /// </summary>
    public static bool IsStale(TimeSpan interval, DateTime nowUtc, DateTime lastRunUtc)
        => nowUtc - lastRunUtc > interval * 2.5 + TimeSpan.FromMinutes(1);

    /// <summary>Per-job last-start / cadence / staleness for the health endpoint.</summary>
    public IReadOnlyList<JobHealth> Snapshot(DateTime nowUtc)
    {
        var list = new List<JobHealth>();
        foreach (var job in _options.Jobs.Keys.OrderBy(k => k))
        {
            var interval = GetInterval(job, nowUtc);
            DateTime? last = _lastRunUtc.TryGetValue(job, out var l) ? l : null;
            list.Add(new JobHealth(job, last, (int)interval.TotalSeconds,
                last.HasValue ? (int)(nowUtc - last.Value).TotalSeconds : null,
                last.HasValue && IsStale(interval, nowUtc, last.Value),
                LastWebhookUtc(job)));
        }
        return list;
    }

    public SyncScheduler(SyncSchedulingOptions options, ILogger<SyncScheduler> logger)
    {
        _options = options;
        _logger = logger;
        _timeZone = ResolveTimeZone(options.TimeZoneId, logger);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId, ILogger<SyncScheduler> logger)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning(ex,
                "SyncScheduler could not resolve time zone '{TimeZoneId}' on this host; falling back to UTC. " +
                "Day/night window will be evaluated in UTC until a valid id is configured.", timeZoneId);
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Pure due-ness check, extracted so the scheduling arithmetic is unit-testable without touching
    /// the clock or the last-run dictionary.
    /// </summary>
    public static bool IsDue(TimeSpan interval, DateTime nowUtc, DateTime lastRunUtc)
        => nowUtc - lastRunUtc >= interval;

    /// <summary>True when the given instant falls inside the configured local day window.</summary>
    public bool IsDay(DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), _timeZone);
        return local.Hour >= _options.DayStartHour && local.Hour < _options.DayEndHour;
    }

    /// <summary>
    /// Returns true and records this instant as the job's last run when the configured interval for the
    /// current day/night window has elapsed; otherwise false (the caller should return without doing work).
    /// Unknown job names default to "always run" so a config gap fails open rather than silently freezing a sync.
    /// </summary>
    public bool TryBeginRun(string job, DateTime nowUtc)
    {
        var interval = GetInterval(job, nowUtc);
        var lastRun = _lastRunUtc.TryGetValue(job, out var last) ? last : DateTime.MinValue;

        if (!IsDue(interval, nowUtc, lastRun))
        {
            // Event-driven early run (JD webhook): honour it once, but never closer than RunNowMinGap to the
            // previous run. A request that arrives too early stays queued for a later tick.
            if (!(_runNow.ContainsKey(job) && nowUtc - lastRun >= RunNowMinGap && _runNow.TryRemove(job, out _)))
                return false;
        }
        else
        {
            _runNow.TryRemove(job, out _); // the scheduled run covers the request
        }

        _lastRunUtc[job] = nowUtc;
        return true;
    }

    /// <summary>Max Uniconta session age for the current day/night window.</summary>
    public TimeSpan GetSessionMaxAge(DateTime nowUtc)
        => TimeSpan.FromSeconds(IsDay(nowUtc) ? _options.SessionMaxAgeDaySeconds : _options.SessionMaxAgeNightSeconds);

    private TimeSpan GetInterval(string job, DateTime nowUtc)
    {
        if (!_options.Jobs.TryGetValue(job, out var cadence))
        {
            _logger.LogWarning("SyncScheduler has no cadence configured for job '{Job}'; running it every tick.", job);
            return TimeSpan.Zero;
        }

        var seconds = IsDay(nowUtc) ? cadence.DaySeconds : cadence.NightSeconds;
        return TimeSpan.FromSeconds(seconds);
    }
}

public sealed record JobHealth(string Job, DateTime? LastStartUtc, int IntervalSeconds, int? SecondsSinceLastStart, bool Stale, DateTime? LastWebhookUtc);

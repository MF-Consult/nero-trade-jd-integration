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
            return false;

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

using System.Runtime.CompilerServices;

namespace NeroTrade.JDIntegration.Services.UnicontaHandler;

/// <summary>
/// Counts Uniconta API calls made during one sync invocation, so the daily call budget is a measurement
/// instead of an estimate.
///
/// Uniconta bills/limits on call volume and the agreed ceiling is a per-day number, but nothing in the
/// system counted them: sizing a cadence change meant multiplying run counts by an assumed calls-per-run.
/// Every Uniconta call now funnels through <see cref="UnicontaConnectionManager.RunWithTimeoutAsync"/>
/// (reads and writes via <c>UnicontaRepository.Timed</c>, plus Login/OpenCompany), which is the one place
/// that can count them.
///
/// The count is per logical invocation, held in an <see cref="AsyncLocal{T}"/> opened by
/// <see cref="Logging.IntegrationRun"/> and written to that run's completion row as
/// <c>payload.uniconta_calls</c>. Per-invocation rather than global because the connection manager is a
/// singleton shared by concurrently running syncs — a global counter could not attribute calls to a job.
/// Calls made outside a run scope (e.g. an admin endpoint) are simply not counted.
/// </summary>
public static class UnicontaCallCounter
{
    private static readonly AsyncLocal<StrongBox<int>?> Current = new();

    /// <summary>Starts counting for the current logical invocation. Called by <see cref="Logging.IntegrationRun"/>.</summary>
    internal static void BeginScope() => Current.Value = new StrongBox<int>(0);

    /// <summary>Records one Uniconta API call. No-op outside a run scope.</summary>
    public static void Increment()
    {
        var counter = Current.Value;
        if (counter is not null) Interlocked.Increment(ref counter.Value);
    }

    /// <summary>Calls counted so far in the current invocation; 0 outside a scope.</summary>
    public static int Count => Current.Value?.Value ?? 0;
}

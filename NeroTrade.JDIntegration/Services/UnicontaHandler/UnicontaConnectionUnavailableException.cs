namespace NeroTrade.JDIntegration.Services.UnicontaHandler;

/// <summary>
/// Uniconta could not be reached for this tick — login or <c>OpenCompany</c> came back empty/failed even
/// after the immediate retry.
///
/// This is deliberately its own type so it can be classified as a <b>warning</b> rather than an error:
/// it is a Uniconta-side transient (most often <c>OpenCompany</c> returning no company for a perfectly
/// valid company id), there is nothing to fix on our side, and the next scheduled tick reconnects on its
/// own. Filed as an error it produced recurring red rows in <c>integration_logs</c> and failed function
/// invocations in App Insights that no one could act on — noise that hides real failures.
///
/// A genuine, actionable connection problem (bad credentials, malformed API key) does <b>not</b> use this
/// type and still surfaces as an error.
/// </summary>
public class UnicontaConnectionUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// A Uniconta SDK call did not return within the configured per-call timeout
/// (<c>UnicontaConfig.TimeoutSeconds</c>).
///
/// The SDK takes no <see cref="CancellationToken"/> and has no timeout of its own, so a call against a
/// dead socket waits forever. Before this existed, such a call burned the Function App's entire 30-minute
/// <c>FunctionTimeout</c> — 14 times in the 30 days to 2026-07-27 — during which the timer trigger for
/// that sync was blocked (singleton), and the host then force-restarted the language worker. Now the wait
/// is bounded, the session is invalidated so the next tick reconnects, and the tick is skipped.
///
/// Derives from <see cref="UnicontaConnectionUnavailableException"/> deliberately: to a sync function this
/// is the same situation — Uniconta is not usable this tick, nothing is actionable, retry next tick — so
/// the existing catch handles it and it is logged as a warning, just with its own error code.
/// </summary>
public sealed class UnicontaCallTimeoutException(string operation, TimeSpan timeout)
    : UnicontaConnectionUnavailableException($"Uniconta call '{operation}' did not return within {timeout.TotalSeconds:0} s")
{
    public string Operation { get; } = operation;
    public TimeSpan Timeout { get; } = timeout;
}

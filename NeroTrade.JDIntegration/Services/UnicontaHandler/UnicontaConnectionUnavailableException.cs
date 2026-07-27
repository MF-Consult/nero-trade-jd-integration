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
public sealed class UnicontaConnectionUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

using System.Net;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;
using NeroTrade.JDIntegration.Services.UnicontaHandler;

namespace NeroTrade.JDIntegration.Services.Logging;

/// <summary>
/// Classification of a run-ending exception. <paramref name="Level"/> is what the completion row is
/// written at: <c>"error"</c> for everything actionable, <c>"warning"</c> for failures that are outside
/// our control and self-heal on the next tick — those must not sit in dashboards as red rows nobody can
/// act on.
/// </summary>
public sealed record ClassifiedError(string ErrorCode, bool Retryable, string SuggestedAction, string Level = "error");

public static class ErrorCodeClassifier
{
    public static ClassifiedError Classify(Exception ex)
    {
        // A Uniconta SDK call blew its per-call timeout. Checked before the base type below so it keeps its
        // own code — the distinction matters: unavailable = Uniconta said no, timeout = Uniconta said
        // nothing at all, which is the shape that used to hang an invocation for 30 minutes.
        if (ex is UnicontaCallTimeoutException timeout)
        {
            return new ClassifiedError(
                "UNICONTA_TIMEOUT",
                Retryable: true,
                SuggestedAction: $"Uniconta call '{timeout.Operation}' did not answer within {timeout.Timeout.TotalSeconds:0}s. The session was invalidated and the next tick reconnects. Investigate only if it repeats across many ticks.",
                Level: "warning");
        }

        // Uniconta was unreachable for this tick (typically OpenCompany returning no company for a valid
        // id). Nothing to fix on our side and the next tick reconnects, so it is a warning, not an error.
        if (ex is UnicontaConnectionUnavailableException)
        {
            return new ClassifiedError(
                "UNICONTA_CONNECT_FAILED",
                Retryable: true,
                SuggestedAction: "Uniconta-side transient — no action needed; the next scheduled tick reconnects. Investigate only if it persists across many consecutive ticks.",
                Level: "warning");
        }

        if (ex is JdLookupFailedException jdLookup)
        {
            return new ClassifiedError(
                "JD_LOOKUP_FAILED",
                Retryable: true,
                SuggestedAction: $"JD GET {jdLookup.Endpoint} returned status {jdLookup.StatusCode} after retries. Cache will serve stale on subsequent ticks; investigate JD side if it persists.");
        }

        // "The request message was already sent" is HttpClient's signal that an HttpRequestMessage
        // (or its HttpContent stream) was reused across SendAsync calls. After the 2026-05 fix that
        // moved request+content allocation inside SendWithRetryAsync's lambda, this should never
        // fire again. If it does, it's a regression — flag it distinctly so a transient retry
        // doesn't get filed as a generic SYNC_RUN_FAILED and hide a real bug.
        if (ex is InvalidOperationException ioe
            && (ioe.Message.Contains("request message was already sent", StringComparison.OrdinalIgnoreCase)
                || ioe.Message.Contains("content has already been read", StringComparison.OrdinalIgnoreCase)))
        {
            return new ClassifiedError(
                "HTTP_REQUEST_REUSE_BUG",
                Retryable: false,
                SuggestedAction: "Regression: a JD repository call reused an HttpRequestMessage or HttpContent across a SendWithRetryAsync attempt. Audit the recently-touched JdRepository methods.");
        }

        if (ex is TaskCanceledException or OperationCanceledException
            || ex.InnerException is TimeoutException)
        {
            return new ClassifiedError(
                "JD_TIMEOUT",
                Retryable: true,
                SuggestedAction: "Transient — next scheduled tick will retry. Investigate if it persists.");
        }

        if (ex is HttpRequestException http)
        {
            var status = http.StatusCode;
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ClassifiedError(
                    "JD_AUTH_FAILED",
                    Retryable: false,
                    SuggestedAction: "Rotate the JD bearer token in app settings — auth was rejected.");
            }
            if (status == HttpStatusCode.TooManyRequests)
            {
                return new ClassifiedError(
                    "JD_RATE_LIMITED",
                    Retryable: true,
                    SuggestedAction: "JD throttled the integration; next tick will retry after the rate window resets.");
            }
            if (status.HasValue && (int)status >= 500)
            {
                return new ClassifiedError(
                    "JD_5XX",
                    Retryable: true,
                    SuggestedAction: "JD-side failure; next scheduled tick will retry.");
            }
        }

        return new ClassifiedError(
            "SYNC_RUN_FAILED",
            Retryable: true,
            SuggestedAction: "Inspect stack trace in App Insights; if transient (timeout/network), the next tick will retry automatically.");
    }
}

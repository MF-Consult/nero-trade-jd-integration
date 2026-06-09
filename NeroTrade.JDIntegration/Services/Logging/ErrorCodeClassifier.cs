using System.Net;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;

namespace NeroTrade.JDIntegration.Services.Logging;

public sealed record ClassifiedError(string ErrorCode, bool Retryable, string SuggestedAction);

public static class ErrorCodeClassifier
{
    public static ClassifiedError Classify(Exception ex)
    {
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

using System.Net;

namespace NeroTrade.JDIntegration.Services.Logging;

public sealed record ClassifiedError(string ErrorCode, bool Retryable, string SuggestedAction);

public static class ErrorCodeClassifier
{
    public static ClassifiedError Classify(Exception ex)
    {
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

using System.Net;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;
using NeroTrade.JDIntegration.Services.Logging;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pin the mapping from common exception shapes to error_code values. Other code in this repo
/// (Hermes seed playbooks, dashboards) filters on these strings, so changing them silently is
/// expensive — these tests guard against accidental reshuffles.
/// </summary>
public class ErrorCodeClassifierTests
{
    [Fact]
    public void JdLookupFailedException_ClassifiedAsJdLookupFailed()
    {
        var ex = new JdLookupFailedException("inventories", 200, "Empty inventories list");

        var classified = ErrorCodeClassifier.Classify(ex);

        Assert.Equal("JD_LOOKUP_FAILED", classified.ErrorCode);
        Assert.True(classified.Retryable);
        Assert.Contains("inventories", classified.SuggestedAction);
    }

    [Fact]
    public void RequestMessageAlreadySent_ClassifiedAsHttpRequestReuseBug()
    {
        var ex = new InvalidOperationException("The request message was already sent. Cannot send the same request message multiple times.");

        var classified = ErrorCodeClassifier.Classify(ex);

        Assert.Equal("HTTP_REQUEST_REUSE_BUG", classified.ErrorCode);
        Assert.False(classified.Retryable);
    }

    [Fact]
    public void TaskCanceled_ClassifiedAsJdTimeout()
    {
        var ex = new TaskCanceledException("Operation timed out");

        var classified = ErrorCodeClassifier.Classify(ex);

        Assert.Equal("JD_TIMEOUT", classified.ErrorCode);
        Assert.True(classified.Retryable);
    }

    [Fact]
    public void HttpRequest401_ClassifiedAsJdAuthFailed()
    {
        var ex = new HttpRequestException("auth", null, HttpStatusCode.Unauthorized);

        var classified = ErrorCodeClassifier.Classify(ex);

        Assert.Equal("JD_AUTH_FAILED", classified.ErrorCode);
        Assert.False(classified.Retryable);
    }

    [Fact]
    public void HttpRequest429_ClassifiedAsJdRateLimited()
    {
        var ex = new HttpRequestException("throttled", null, HttpStatusCode.TooManyRequests);

        var classified = ErrorCodeClassifier.Classify(ex);

        Assert.Equal("JD_RATE_LIMITED", classified.ErrorCode);
        Assert.True(classified.Retryable);
    }

    [Fact]
    public void HttpRequest503_ClassifiedAsJd5xx()
    {
        var ex = new HttpRequestException("server", null, HttpStatusCode.ServiceUnavailable);

        var classified = ErrorCodeClassifier.Classify(ex);

        Assert.Equal("JD_5XX", classified.ErrorCode);
        Assert.True(classified.Retryable);
    }

    [Fact]
    public void GenericException_ClassifiedAsSyncRunFailed()
    {
        var ex = new InvalidOperationException("some unrelated boom");

        var classified = ErrorCodeClassifier.Classify(ex);

        Assert.Equal("SYNC_RUN_FAILED", classified.ErrorCode);
        Assert.True(classified.Retryable);
    }
}

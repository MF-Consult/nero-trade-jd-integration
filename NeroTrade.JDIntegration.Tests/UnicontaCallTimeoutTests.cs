using Microsoft.Extensions.Logging.Abstractions;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.Scheduling;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the per-call Uniconta timeout added after the 2026-07 hung-invocation incidents: 14 invocations in
/// 30 days sat on a Uniconta call that never returned, each burning the Function App's 30-minute ceiling
/// and blocking that sync's timer for the whole period before the host force-restarted the worker.
/// The SDK takes no CancellationToken, so the guarantee is "we stop waiting", not "the call is aborted".
/// </summary>
public class UnicontaCallTimeoutTests
{
    [Fact]
    public async Task CallThatNeverReturns_ThrowsInsteadOfHanging()
    {
        var manager = BuildManager();
        var neverCompletes = new TaskCompletionSource<int>().Task;

        var ex = await Assert.ThrowsAsync<UnicontaCallTimeoutException>(
            () => manager.RunWithTimeoutAsync(neverCompletes, "Query<DebtorClient>", TimeSpan.FromMilliseconds(50)));

        Assert.Equal("Query<DebtorClient>", ex.Operation);
        Assert.Contains("Query<DebtorClient>", ex.Message);
    }

    [Fact]
    public async Task TimeoutIsTreatedAsUnicontaUnavailable_SoTheSyncsExistingCatchHandlesIt()
    {
        // The syncs catch UnicontaConnectionUnavailableException and skip the tick with a warning.
        // A timeout must land in that same catch — that is why it derives from it.
        var manager = BuildManager();
        var neverCompletes = new TaskCompletionSource<int>().Task;

        await Assert.ThrowsAsync<UnicontaCallTimeoutException>(
            () => manager.RunWithTimeoutAsync<int>(neverCompletes, "op", TimeSpan.FromMilliseconds(50)));

        var caught = false;
        try
        {
            await manager.RunWithTimeoutAsync<int>(neverCompletes, "op", TimeSpan.FromMilliseconds(50));
        }
        catch (UnicontaConnectionUnavailableException)
        {
            caught = true;
        }
        Assert.True(caught);
    }

    [Fact]
    public async Task CallThatCompletesInTime_ReturnsItsResult()
    {
        var manager = BuildManager();

        var result = await manager.RunWithTimeoutAsync(Task.FromResult(42), "op", TimeSpan.FromSeconds(30));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task CallThatFailsInTime_SurfacesItsOwnException_NotATimeout()
    {
        // A real SDK error must not be reshaped into a timeout — it has its own (often actionable) meaning.
        var manager = BuildManager();
        var failing = Task.FromException<int>(new InvalidOperationException("Uniconta said no"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RunWithTimeoutAsync(failing, "op", TimeSpan.FromSeconds(30)));

        Assert.Equal("Uniconta said no", ex.Message);
    }

    [Fact]
    public void TimeoutClassifiesAsWarningWithItsOwnCode()
    {
        var classified = ErrorCodeClassifier.Classify(
            new UnicontaCallTimeoutException("Query<CreditorOrderClient>", TimeSpan.FromSeconds(30)));

        Assert.Equal("UNICONTA_TIMEOUT", classified.ErrorCode);
        Assert.Equal("warning", classified.Level);
        Assert.True(classified.Retryable);
        Assert.Contains("Query<CreditorOrderClient>", classified.SuggestedAction);
    }

    private static UnicontaConnectionManager BuildManager() =>
        new(NullLogger<UnicontaConnectionManager>.Instance,
            new UnicontaConfig { Username = "u", Password = "p", ApiKey = Guid.NewGuid().ToString(), CompanyId = 1 },
            new SyncScheduler(new SyncSchedulingOptions(), NullLogger<SyncScheduler>.Instance));
}

using NeroTrade.JDIntegration.Services.Logging;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Verifies the <c>exit_reason</c> contract on the completion row. Dead-window diagnosis needs to
/// distinguish "0 ms tick because nothing was eligible" from "0 ms tick because a gate
/// short-circuited" — those two are indistinguishable on duration alone.
/// </summary>
public class IntegrationRunTests
{
    [Fact]
    public async Task DisposeAsync_DefaultsExitReasonToCompleted()
    {
        var logger = new RecordingIntegrationLogger();

        await using (var run = logger.BeginRun("TestRun"))
        {
            // No ExitReason set, no failure → defaults to "completed".
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("info", entry.Level);
        Assert.Equal("completed", entry.Payload!.Value.GetProperty("exit_reason").GetString());
    }

    [Fact]
    public async Task DisposeAsync_RespectsExplicitExitReason()
    {
        var logger = new RecordingIntegrationLogger();

        await using (var run = logger.BeginRun("TestRun"))
        {
            run.ExitReason = "no_eligible_orders";
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("no_eligible_orders", entry.Payload!.Value.GetProperty("exit_reason").GetString());
    }

    [Fact]
    public async Task DisposeAsync_FailureDefaultsExitReasonToRunFailed()
    {
        var logger = new RecordingIntegrationLogger();

        await using (var run = logger.BeginRun("TestRun"))
        {
            run.MarkFailed(new InvalidOperationException("boom"));
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("error", entry.Level);
        Assert.Equal("run_failed", entry.Payload!.Value.GetProperty("exit_reason").GetString());
    }

    [Fact]
    public async Task DisposeAsync_AttachedPayload_AppearsAsCountsAlongsideExitReason()
    {
        var logger = new RecordingIntegrationLogger();

        await using (var run = logger.BeginRun("TestRun"))
        {
            run.ExitReason = "completed";
            run.AttachCompletionPayload(new { processed = 3, succeeded = 2, failed = 1 });
        }

        var entry = Assert.Single(logger.Entries);
        var payload = entry.Payload!.Value;
        Assert.Equal("completed", payload.GetProperty("exit_reason").GetString());
        Assert.Equal(3, payload.GetProperty("counts").GetProperty("processed").GetInt32());
    }

    /// <summary>
    /// Test double for <see cref="IIntegrationLogger"/> that records every <see cref="LogAsync"/>
    /// call so the test can assert on the completion row. <see cref="BeginRun"/> mirrors
    /// <c>SupabaseIntegrationLogger.BeginRun</c>: it invokes <see cref="IntegrationRun"/>'s
    /// internal constructor, which the test project sees via <c>InternalsVisibleTo</c>.
    /// </summary>
    private sealed class RecordingIntegrationLogger : IIntegrationLogger
    {
        public List<IntegrationLogEntry> Entries { get; } = new();

        public string IntegrationName => "NeroTrade.JDIntegration.Tests";

        public Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public IntegrationRun BeginRun(string runName)
            => new(this, IntegrationName, runName);

        public Task MarkResolvedAsync(string integrationName, string externalId, Guid successCorrelationId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

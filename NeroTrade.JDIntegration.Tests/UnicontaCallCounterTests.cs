using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// The daily Uniconta call budget is a hard number (agreed ceiling per day), but until now nothing counted
/// the calls — sizing a cadence change meant multiplying run counts by an assumed calls-per-run. These pin
/// the counter that turns that estimate into a measurement on every run's completion row.
/// </summary>
public class UnicontaCallCounterTests
{
    [Fact]
    public async Task CompletionRow_CarriesTheCallCountForThatRun()
    {
        var logger = new RecordingIntegrationLogger();

        await using (var run = logger.BeginRun("SyncPurchaseOrdersToJd"))
        {
            UnicontaCallCounter.Increment();
            UnicontaCallCounter.Increment();
            UnicontaCallCounter.Increment();
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(3, entry.Payload!.Value.GetProperty("uniconta_calls").GetInt32());
    }

    [Fact]
    public async Task CountsSurviveAwaits_SoCallsDeepInTheSyncStillCount()
    {
        // The counter is AsyncLocal: a call made after several awaits, inside a nested async method,
        // must still be attributed to the run that opened the scope.
        var logger = new RecordingIntegrationLogger();

        await using (var run = logger.BeginRun("SyncSalesOrdersToJd"))
        {
            await Task.Yield();
            await NestedCallAsync();
            await Task.Delay(1);
            UnicontaCallCounter.Increment();
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(2, entry.Payload!.Value.GetProperty("uniconta_calls").GetInt32());

        static async Task NestedCallAsync()
        {
            await Task.Yield();
            UnicontaCallCounter.Increment();
        }
    }

    [Fact]
    public async Task EachRunCountsOnlyItsOwnCalls()
    {
        // The connection manager is a singleton shared by concurrently running syncs, so the count must be
        // per invocation — otherwise one job's calls would be billed to another's row.
        var logger = new RecordingIntegrationLogger();

        await using (var first = logger.BeginRun("RunA"))
        {
            UnicontaCallCounter.Increment();
        }

        await using (var second = logger.BeginRun("RunB"))
        {
            UnicontaCallCounter.Increment();
            UnicontaCallCounter.Increment();
        }

        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(1, logger.Entries[0].Payload!.Value.GetProperty("uniconta_calls").GetInt32());
        Assert.Equal(2, logger.Entries[1].Payload!.Value.GetProperty("uniconta_calls").GetInt32());
    }

    [Fact]
    public async Task RunThatTouchesUniconta_NotAtAll_ReportsZero()
    {
        var logger = new RecordingIntegrationLogger();

        await using (var run = logger.BeginRun("SyncItemsToJd")) { }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(0, entry.Payload!.Value.GetProperty("uniconta_calls").GetInt32());
    }

    private sealed class RecordingIntegrationLogger : IIntegrationLogger
    {
        public List<IntegrationLogEntry> Entries { get; } = new();
        public string IntegrationName => "NeroTrade.JDIntegration.Tests";
        public Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public IntegrationRun BeginRun(string runName) => new(this, IntegrationName, runName);
        public Task MarkResolvedAsync(string integrationName, string externalId, Guid successCorrelationId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.JdWebhook;
using NeroTrade.JDIntegration.Services.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

public class WebhookReconciliationTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoWebhookConfigured_NeverFlagsAMiss() =>
        Assert.False(WebhookReconciliation.IsMissed(false, null, Now, changesFoundByPoll: 3));

    [Fact]
    public void NothingFoundByPoll_IsNotAMiss() =>
        Assert.False(WebhookReconciliation.IsMissed(true, null, Now, changesFoundByPoll: 0));

    [Fact]
    public void PollFindsChanges_NoWebhookEver_IsAMiss() =>
        Assert.True(WebhookReconciliation.IsMissed(true, null, Now, 1));

    [Fact]
    public void PollFindsChanges_RecentWebhook_IsNotAMiss() =>
        Assert.False(WebhookReconciliation.IsMissed(true, Now.AddMinutes(-3), Now, 1));

    [Fact]
    public void PollFindsChanges_OldWebhook_IsAMiss() =>
        Assert.True(WebhookReconciliation.IsMissed(true, Now.AddMinutes(-30), Now, 1));

    [Fact]
    public void Scheduler_RecordsLastWebhookPerJob()
    {
        var s = new SyncScheduler(new SyncSchedulingOptions(), NullLogger<SyncScheduler>.Instance);
        Assert.Null(s.LastWebhookUtc("RequestOrderStatus"));
        s.RecordWebhook("RequestOrderStatus", Now);
        Assert.Equal(Now, s.LastWebhookUtc("RequestOrderStatus"));
        Assert.Null(s.LastWebhookUtc("ReceivedQuantity"));
    }

    [Fact]
    public void FullSweep_IsDueFirstTime_ThenOnlyAfterTheInterval()
    {
        var s = new SyncScheduler(new SyncSchedulingOptions(), NullLogger<SyncScheduler>.Instance);
        var every = TimeSpan.FromHours(24);
        Assert.True(s.IsFullSweepDue("RequestOrderStatus", Now, every));
        s.MarkFullSweep("RequestOrderStatus", Now);
        Assert.False(s.IsFullSweepDue("RequestOrderStatus", Now.AddHours(23), every));
        Assert.True(s.IsFullSweepDue("RequestOrderStatus", Now.AddHours(24), every));
    }

    [Fact]
    public void Stale_MeansMuchLongerThanTheCadence()
    {
        var interval = TimeSpan.FromSeconds(72);
        Assert.False(SyncScheduler.IsStale(interval, Now, Now.AddSeconds(-150)));
        Assert.True(SyncScheduler.IsStale(interval, Now, Now.AddMinutes(-10)));
    }

    [Fact]
    public void Snapshot_ListsEveryConfiguredJob()
    {
        var opts = new SyncSchedulingOptions();
        var s = new SyncScheduler(opts, NullLogger<SyncScheduler>.Instance);
        s.TryBeginRun("SalesOrders", Now);
        var snap = s.Snapshot(Now.AddSeconds(30));
        Assert.Equal(opts.Jobs.Count, snap.Count);
        var so = snap.Single(j => j.Job == "SalesOrders");
        Assert.Equal(30, so.SecondsSinceLastStart);
        Assert.False(so.Stale);
        Assert.Null(snap.First(j => j.Job != "SalesOrders").LastStartUtc);
    }
}

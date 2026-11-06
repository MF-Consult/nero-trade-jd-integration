using Microsoft.Extensions.Logging.Abstractions;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.JdWebhook;
using NeroTrade.JDIntegration.Services.Scheduling;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the inbound JD webhook contract: only calls signed with the webhook secret are honoured, only the
/// request-order and incoming-shipment events trigger work, and an event-driven run is bounded (once per
/// <see cref="SyncScheduler.RunNowMinGap"/>) so a burst of events cannot blow the Uniconta call budget.
/// </summary>
public class JdWebhookTests
{
    private const string Secret = "s3cret-from-jd";
    private const string JobId = "693bcb52-5b03-4983-b9eb-c4e706807cde";

    [Fact]
    public void Signature_Hex_IsAccepted_InEitherCase()
    {
        var hex = JdWebhookSignature.ComputeHex(JobId, Secret);
        Assert.True(JdWebhookSignature.IsValid(JobId, hex, Secret));
        Assert.True(JdWebhookSignature.IsValid(JobId, hex.ToUpperInvariant(), Secret));
    }

    [Fact]
    public void Signature_Base64_IsAccepted()
    {
        var b64 = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(JobId)));
        Assert.True(JdWebhookSignature.IsValid(JobId, b64, Secret));
    }

    [Theory]
    [InlineData("deadbeef")]
    [InlineData("not base64 or hex!!")]
    [InlineData("")]
    [InlineData(null)]
    public void Signature_WrongOrMissingHash_IsRejected(string? hash)
        => Assert.False(JdWebhookSignature.IsValid(JobId, hash, Secret));

    [Fact]
    public void Signature_ForDifferentJobId_IsRejected()
    {
        var hexForOther = JdWebhookSignature.ComputeHex("other-job", Secret);
        Assert.False(JdWebhookSignature.IsValid(JobId, hexForOther, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Signature_EmptySecret_NeverValidates_FailClosed(string? secret)
    {
        // Even a hash computed with an empty key must not pass: no secret configured = endpoint closed.
        var hash = Convert.ToHexString(HMACSHA256.HashData(Array.Empty<byte>(), Encoding.UTF8.GetBytes(JobId)));
        Assert.False(JdWebhookSignature.IsValid(JobId, hash, secret));
    }

    [Theory]
    [InlineData(0, "RequestOrderStatus")]
    [InlineData(3, "RequestOrderStatus")]
    [InlineData(30, "ReceivedQuantity")]
    [InlineData(32, "ReceivedQuantity")]
    [InlineData(10, null)]   // Inventory_Updated
    [InlineData(21, null)]   // Catalog_Updated
    [InlineData(999, null)]
    public void Router_MapsWebhookTypesToJobs(int type, string? expectedJob)
        => Assert.Equal(expectedJob, JdWebhookRouter.JobFor(type));

    [Fact]
    public void RunNow_TriggersAnOtherwiseNotDueJob_OnceOnly()
    {
        var scheduler = new SyncScheduler(new SyncSchedulingOptions(), NullLogger<SyncScheduler>.Instance);
        var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(scheduler.TryBeginRun("RequestOrderStatus", t0));            // normal first run
        Assert.False(scheduler.TryBeginRun("RequestOrderStatus", t0.AddSeconds(90))); // not due (590 s cadence)

        scheduler.RequestRunNow("RequestOrderStatus");
        Assert.True(scheduler.TryBeginRun("RequestOrderStatus", t0.AddSeconds(120)));  // webhook: runs now
        Assert.False(scheduler.TryBeginRun("RequestOrderStatus", t0.AddSeconds(150))); // consumed, not due again
    }

    [Fact]
    public void RunNow_IsHeldBackUntilMinGap_ThenHonoured()
    {
        var scheduler = new SyncScheduler(new SyncSchedulingOptions(), NullLogger<SyncScheduler>.Instance);
        var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(scheduler.TryBeginRun("ReceivedQuantity", t0));

        scheduler.RequestRunNow("ReceivedQuantity");
        Assert.False(scheduler.TryBeginRun("ReceivedQuantity", t0.AddSeconds(30)));  // < 60 s since last run: queued
        Assert.True(scheduler.TryBeginRun("ReceivedQuantity", t0.AddSeconds(60)));   // gap reached: runs
    }

    [Fact]
    public void RunNow_IsDroppedWhenTheScheduledRunHappensFirst()
    {
        var scheduler = new SyncScheduler(new SyncSchedulingOptions(), NullLogger<SyncScheduler>.Instance);
        var t0 = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(scheduler.TryBeginRun("RequestOrderStatus", t0));

        scheduler.RequestRunNow("RequestOrderStatus");
        Assert.True(scheduler.TryBeginRun("RequestOrderStatus", t0.AddSeconds(600))); // scheduled run covers it
        Assert.False(scheduler.TryBeginRun("RequestOrderStatus", t0.AddSeconds(660))); // request not replayed
    }
}

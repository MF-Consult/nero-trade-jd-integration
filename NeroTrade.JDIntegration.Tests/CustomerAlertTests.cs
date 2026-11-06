using Microsoft.Extensions.Logging.Abstractions;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Alerts;
using NeroTrade.JDIntegration.Services.Logging;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>
/// Pins the customer error mail: which rows trigger one, what it says, that it is sent once per problem per
/// window, and — most important — that a mail failure can never break or even delay the underlying log write.
/// </summary>
[Collection("CustomerAlertDedup")] // the dedup store is process-wide
public class CustomerAlertTests
{
    private static IntegrationLogEntry PoRejected(string po = "98") => new(
        "NeroTrade.JDIntegration", "error", "JD", po,
        $"JD rejected purchase order {po}: No matching JD catalog item for SKU(s): DKABNPSCUP200WH. The item must exist in JD's catalog (synced via SyncItemsToJd) before this purchase order can be received.",
        null, null)
    { ErrorCode = "JD_VALIDATION_REJECTED" };

    private static CustomerAlertOptions Enabled() => new()
    { Enabled = true, ResendApiKey = "k", From = "a@b.dk", To = "c@d.dk" };

    [Fact]
    public void MissingCatalogItem_IsWordedInPlainDanish_WithTheSkuAndTheFix()
    {
        var alert = CustomerAlertFormatter.TryFormat(PoRejected())!;

        Assert.Equal("Indkøb 98 blev ikke sendt til JD", alert.Subject);
        Assert.Contains("DKABNPSCUP200WH", alert.Body);
        Assert.Contains("Overfør vare", alert.Body);
        Assert.DoesNotContain("SyncItemsToJd", alert.Body); // no internal jargon in a customer mail
    }

    [Theory]
    [InlineData("warning", "JD_VALIDATION_REJECTED")]
    [InlineData("error", "JD_TIMEOUT")]
    [InlineData("error", "SYNC_RUN_FAILED")]
    [InlineData("error", null)]
    public void Rows_TheCustomerCannotActOn_AreNotMailed(string level, string? code)
    {
        var entry = new IntegrationLogEntry("n", level, "JD", "98", "boom", null, null) { ErrorCode = code };
        Assert.Null(CustomerAlertFormatter.TryFormat(entry));
    }

    [Fact]
    public void RowWithoutExternalId_IsNotMailed()
        => Assert.Null(CustomerAlertFormatter.TryFormat(PoRejected(" ") with { ExternalId = null }));

    [Fact]
    public async Task SameProblem_IsMailedOncePerWindow_AndAgainAfterIt()
    {
        CustomerAlertingLogger.ResetDedupForTests();
        var sender = new RecordingSender();
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var logger = new CustomerAlertingLogger(new RecordingLogger(), sender, Enabled(), NullLogger<CustomerAlertingLogger>.Instance, () => now);

        await logger.LogAsync(PoRejected(), default);
        await logger.LogAsync(PoRejected(), default);
        Assert.Single(sender.Sent);

        now = now.AddHours(25);
        await logger.LogAsync(PoRejected(), default);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task OtherOrder_IsMailedSeparately()
    {
        CustomerAlertingLogger.ResetDedupForTests();
        var sender = new RecordingSender();
        var logger = new CustomerAlertingLogger(new RecordingLogger(), sender, Enabled(), NullLogger<CustomerAlertingLogger>.Instance);

        await logger.LogAsync(PoRejected("98"), default);
        await logger.LogAsync(PoRejected("99"), default);

        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task MailFailure_DoesNotBreakLogging_AndAllowsARetry()
    {
        CustomerAlertingLogger.ResetDedupForTests();
        var inner = new RecordingLogger();
        var sender = new RecordingSender { Fail = true };
        var logger = new CustomerAlertingLogger(inner, sender, Enabled(), NullLogger<CustomerAlertingLogger>.Instance);

        await logger.LogAsync(PoRejected(), default); // must not throw
        Assert.Single(inner.Rows);                    // the row itself was still written
        Assert.Empty(sender.Sent);

        sender.Fail = false;
        await logger.LogAsync(PoRejected(), default); // a failed send did not use up the window
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Disabled_WritesTheRowButSendsNothing()
    {
        CustomerAlertingLogger.ResetDedupForTests();
        var inner = new RecordingLogger();
        var sender = new RecordingSender();
        var logger = new CustomerAlertingLogger(inner, sender, new CustomerAlertOptions { Enabled = false }, NullLogger<CustomerAlertingLogger>.Instance);

        await logger.LogAsync(PoRejected(), default);

        Assert.Single(inner.Rows);
        Assert.Empty(sender.Sent);
    }

    private sealed class RecordingSender : ICustomerAlertSender
    {
        public bool Fail { get; set; }
        public List<(string Subject, string Body)> Sent { get; } = new();
        public Task SendAsync(string subject, string textBody, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("resend down");
            Sent.Add((subject, textBody));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : IIntegrationLogger
    {
        public List<IntegrationLogEntry> Rows { get; } = new();
        public string IntegrationName => "NeroTrade.JDIntegration";
        public Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken) { Rows.Add(entry); return Task.CompletedTask; }
        public IntegrationRun BeginRun(string runName) => throw new NotSupportedException();
        public Task MarkResolvedAsync(string integrationName, string externalId, Guid successCorrelationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

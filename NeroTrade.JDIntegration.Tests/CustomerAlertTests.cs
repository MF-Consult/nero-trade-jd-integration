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
    private const string NoCatalogReason =
        "No matching JD catalog item for SKU(s): DKABNPSCUP200WH. The item must exist in JD's catalog (synced via SyncItemsToJd) before this purchase order can be received.";

    private static IntegrationLogEntry PoRejected(string po = "98", string reason = NoCatalogReason) => new(
        "NeroTrade.JDIntegration", "error", "JD", po, $"JD rejected purchase order {po}: {reason}", null, null)
    { ErrorCode = "JD_VALIDATION_REJECTED", AlertSubject = CustomerAlertSubject.PurchaseOrder, AlertReason = reason };

    private static IntegrationLogEntry SoRejected(string so, string reason) => new(
        "NeroTrade.JDIntegration", "error", "JD", so, $"JD rejected sales order {so}: {reason}", null, null)
    { ErrorCode = "JD_VALIDATION_REJECTED", AlertSubject = CustomerAlertSubject.SalesOrder, AlertReason = reason };

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

    [Fact]
    public void MissingCatalogItem_WithAutoCreateOn_DoesNotTellTheCustomerToSetTheFlag()
    {
        var alert = CustomerAlertFormatter.TryFormat(PoRejected(), autoCreateMissingItems: true)!;

        Assert.Contains("ikke kunne oprettes automatisk", alert.Body);
        Assert.DoesNotContain("Sæt \"Overfør vare\"", alert.Body);
    }

    // The reject reasons below are real ones from integration_logs (Sept–Nov 2026).
    [Theory]
    [InlineData("{\"error\":\"We couldn't find any product with the SKU: CHNAIPAPRDEJ16OZ\"}", "varen CHNAIPAPRDEJ16OZ ikke findes hos JD")]
    [InlineData("{\"error\":\"We couldn't fulfill SKU: DKABNAPAIRL484814F-W due to low stock.Current stock: 6, requested stock: 8.\"}", "kun har 6 stk. af varen DKABNAPAIRL484814F-W på lager, og der er bestilt 8")]
    [InlineData("{\"error\":\"The request order doesn't have any inventory items.\"}", "ikke er nogen lagervarer")]
    [InlineData("{\"ContactPerson.Name\":[\"'Contact Person Name' must not be empty.\"]}", "navn på kontaktperson/modtager mangler")]
    public void SalesOrderRejects_AreTranslated(string reason, string expected)
    {
        var alert = CustomerAlertFormatter.TryFormat(SoRejected("2470", reason))!;

        Assert.Equal("Ordre 2470 blev ikke sendt til JD", alert.Subject);
        Assert.Contains(expected, alert.Body);
        Assert.DoesNotContain("couldn't", alert.Body);
        Assert.DoesNotContain("must not", alert.Body);
    }

    [Fact]
    public void PastDeliveryDate_IsExplained()
    {
        var alert = CustomerAlertFormatter.TryFormat(PoRejected(reason: "{\"Date\":[\"'Date' must be greater than or equal to '09/11/2026 00:00:00'.\"]}"))!;
        Assert.Contains("leveringsdatoen ligger før i dag", alert.Body);
    }

    [Fact]
    public void UnknownReason_KeepsJdsTextSoNothingIsLost()
    {
        var alert = CustomerAlertFormatter.TryFormat(SoRejected("1", "{\"error\":\"Something new\"}"))!;
        Assert.Contains("Something new", alert.Body);
    }

    [Fact]
    public void ItemMissingInUniconta_IsMailedEvenThoughItIsLoggedAsWarning()
    {
        var entry = new IntegrationLogEntry("n", "warning", "Uniconta", "DKABNPSCUP200WH", "Item … exists neither in JD nor in the Uniconta item master", null, null)
        { ErrorCode = "UNICONTA_LOOKUP_MISS", AlertSubject = CustomerAlertSubject.CatalogItem };

        var alert = CustomerAlertFormatter.TryFormat(entry)!;
        Assert.Equal("Varen DKABNPSCUP200WH findes ikke i Uniconta", alert.Subject);
    }

    [Fact]
    public void CatalogItemRejected_TranslatesFieldErrors()
    {
        var reason = "{\"ProducedInCountryCode\":[\"'Produced In Country Code' must be between 2 and 2 characters. You entered 7 characters.\"]}";
        var entry = new IntegrationLogEntry("n", "error", "JD", "TRBFTELIPWW610", $"JD rejected catalog item TRBFTELIPWW610: {reason}", null, null)
        { ErrorCode = "JD_VALIDATION_REJECTED", AlertSubject = CustomerAlertSubject.CatalogItem, AlertReason = reason };

        var alert = CustomerAlertFormatter.TryFormat(entry)!;
        Assert.Equal("Varen TRBFTELIPWW610 kunne ikke oprettes hos JD", alert.Subject);
        Assert.Contains("landekode på 2 bogstaver", alert.Body);
    }

    [Theory]
    [InlineData("error", "JD_VALIDATION_REJECTED")] // e.g. a PDF side-issue: the order itself still shipped
    [InlineData("error", "JD_TIMEOUT")]
    [InlineData("error", "SYNC_RUN_FAILED")]
    [InlineData("warning", "JD_CATALOG_ALREADY_EXISTS")]
    [InlineData("error", null)]
    public void Rows_WithoutAnAlertSubject_AreNotMailed(string level, string? code)
    {
        var entry = new IntegrationLogEntry("n", level, "JD", "98", "boom", null, null) { ErrorCode = code };
        Assert.Null(CustomerAlertFormatter.TryFormat(entry));
    }

    [Fact]
    public void RowWithoutExternalId_IsNotMailed()
        => Assert.Null(CustomerAlertFormatter.TryFormat(PoRejected() with { ExternalId = null }));

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
        public Task SendAsync(string subject, string textBody, CancellationToken cancellationToken, IReadOnlyList<string>? toOverride = null)
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

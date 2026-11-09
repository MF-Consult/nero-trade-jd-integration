using System.Text.Json;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Alerts;
using Xunit;

namespace NeroTrade.JDIntegration.Tests;

/// <summary>Pins who a customer mail goes to: To visibly, Bcc hidden, and a test mail only where it was asked.</summary>
public class ResendAlertSenderTests
{
    private static CustomerAlertOptions Options(string? bcc) => new()
    {
        Enabled = true, ResendApiKey = "test", From = "noreply@example.test",
        To = "customer@example.test", Bcc = bcc
    };

    private static JsonElement Payload(HttpRequestMessage request) =>
        JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement;

    [Fact]
    public void Bcc_is_sent_as_hidden_recipients_and_not_on_To()
    {
        using var request = ResendAlertSender.BuildRequest(Options(" ops@example.test , dev@example.test "), "s", "b");
        var json = Payload(request);
        Assert.Equal(["customer@example.test"], json.GetProperty("to").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["ops@example.test", "dev@example.test"], json.GetProperty("bcc").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void No_bcc_field_when_not_configured()
    {
        using var request = ResendAlertSender.BuildRequest(Options(null), "s", "b");
        Assert.False(Payload(request).TryGetProperty("bcc", out _));
    }

    [Fact]
    public void Test_mail_with_explicit_recipient_skips_bcc()
    {
        using var request = ResendAlertSender.BuildRequest(Options("ops@example.test"), "s", "b", ["tester@example.test"]);
        var json = Payload(request);
        Assert.Equal(["tester@example.test"], json.GetProperty("to").EnumerateArray().Select(e => e.GetString()));
        Assert.False(json.TryGetProperty("bcc", out _));
    }
}

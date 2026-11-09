using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NeroTrade.JDIntegration.Models.Settings;

namespace NeroTrade.JDIntegration.Services.Alerts;

public interface ICustomerAlertSender
{
    /// <param name="toOverride">
    /// Recipients to use instead of the configured <c>CustomerAlerts:To</c> (test mails). <c>CustomerAlerts:Bcc</c>
    /// is not added then, so a test goes only where it was asked to.
    /// </param>
    Task SendAsync(string subject, string textBody, CancellationToken cancellationToken, IReadOnlyList<string>? toOverride = null);
}

/// <summary>Sends a plain-text mail through Resend's REST API (<c>POST https://api.resend.com/emails</c>).</summary>
public sealed class ResendAlertSender(HttpClient http, CustomerAlertOptions options) : ICustomerAlertSender
{
    public async Task SendAsync(string subject, string textBody, CancellationToken cancellationToken, IReadOnlyList<string>? toOverride = null)
    {
        using var request = BuildRequest(options, subject, textBody, toOverride);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    internal static HttpRequestMessage BuildRequest(CustomerAlertOptions options, string subject, string textBody, IReadOnlyList<string>? toOverride = null)
    {
        var isOverride = toOverride is { Count: > 0 };
        var to = isOverride ? toOverride!.ToArray() : SplitAddresses(options.To);
        var bcc = isOverride ? Array.Empty<string>() : SplitAddresses(options.Bcc);
        object payload = bcc.Length > 0
            ? new { from = options.From, to, bcc, subject, text = textBody }
            : new { from = options.From, to, subject, text = textBody };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ResendApiKey);
        return request;
    }

    private static string[] SplitAddresses(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

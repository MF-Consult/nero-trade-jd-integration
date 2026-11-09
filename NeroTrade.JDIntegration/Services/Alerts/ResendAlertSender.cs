using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NeroTrade.JDIntegration.Models.Settings;

namespace NeroTrade.JDIntegration.Services.Alerts;

public interface ICustomerAlertSender
{
    /// <param name="toOverride">Recipients to use instead of the configured <c>CustomerAlerts:To</c> (test mails).</param>
    Task SendAsync(string subject, string textBody, CancellationToken cancellationToken, IReadOnlyList<string>? toOverride = null);
}

/// <summary>Sends a plain-text mail through Resend's REST API (<c>POST https://api.resend.com/emails</c>).</summary>
public sealed class ResendAlertSender(HttpClient http, CustomerAlertOptions options) : ICustomerAlertSender
{
    public async Task SendAsync(string subject, string textBody, CancellationToken cancellationToken, IReadOnlyList<string>? toOverride = null)
    {
        var to = toOverride is { Count: > 0 }
            ? toOverride.ToArray()
            : options.To!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { from = options.From, to, subject, text = textBody }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ResendApiKey);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

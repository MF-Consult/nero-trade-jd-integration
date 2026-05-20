using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NeroTrade.JDIntegration.Services.Logging;

public sealed class SupabaseIntegrationLogger : IIntegrationLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly HttpClient _httpClient;
    private readonly ILogger<SupabaseIntegrationLogger> _logger;

    public SupabaseIntegrationLogger(HttpClient httpClient, ILogger<SupabaseIntegrationLogger> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
    {
        var payload = new
        {
            integration_name = entry.IntegrationName,
            level = entry.Level,
            source_system = entry.SourceSystem,
            external_id = entry.ExternalId,
            message = entry.Message,
            stack_trace = entry.StackTrace,
            payload = entry.Payload,
            error_code = entry.ErrorCode,
            correlation_id = entry.CorrelationId,
            retryable = entry.Retryable,
            attempt = entry.Attempt,
            suggested_action = entry.SuggestedAction
            // status defaults to 'open' server-side; resolution + project filled by the agent/server.
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "integration_logs");
            request.Content = JsonContent.Create(payload, options: JsonOptions);
            request.Headers.Add("Prefer", "return=minimal");
            request.Content.Headers.ContentType!.CharSet = Utf8.WebName;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // CRITICAL: Logging must never break the main flow.
            // Fall back to local logger so the issue still surfaces in App Insights.
            _logger.LogError(ex,
                "Failed to write log entry to Supabase. Original entry: {Level} {Source} {Message}",
                entry.Level, entry.SourceSystem, entry.Message);
        }
    }
}

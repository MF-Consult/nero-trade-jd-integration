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
    private readonly string _integrationName;
    private readonly string _project;

    public SupabaseIntegrationLogger(
        HttpClient httpClient,
        SupabaseOptions options,
        ILogger<SupabaseIntegrationLogger> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _integrationName = options.IntegrationName;
        _project = options.Project;
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
            suggested_action = entry.SuggestedAction,
            duration_ms = entry.DurationMs
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

    public IntegrationRun BeginRun(string runName, CancellationToken cancellationToken) =>
        new(this, _integrationName, runName, cancellationToken);

    public async Task MarkResolvedAsync(
        string integrationName,
        string externalId,
        Guid successCorrelationId,
        CancellationToken cancellationToken)
    {
        // PATCH integration_logs?project=eq.X&integration_name=eq.Y&external_id=eq.Z&status=in.(open,ack)
        // status -> 'auto_fixed', resolution jsonb populated with success metadata.
        // Never touches wontfix/resolved (status filter), and scope includes both project AND integration_name
        // so reused external_id across integrations cannot match.
        var query = "integration_logs"
                    + $"?project=eq.{Uri.EscapeDataString(_project)}"
                    + $"&integration_name=eq.{Uri.EscapeDataString(integrationName)}"
                    + $"&external_id=eq.{Uri.EscapeDataString(externalId)}"
                    + "&status=in.(open,ack)";

        var body = new
        {
            status = "auto_fixed",
            resolution = new
            {
                resolved_by = "integration-run-auto-resolve",
                resolved_at = DateTimeOffset.UtcNow,
                success_correlation_id = successCorrelationId
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, query)
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };
            request.Headers.Add("Prefer", "return=minimal");
            request.Content!.Headers.ContentType!.CharSet = Utf8.WebName;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to auto-resolve open failures for {Integration} {ExternalId}",
                integrationName, externalId);
        }
    }
}

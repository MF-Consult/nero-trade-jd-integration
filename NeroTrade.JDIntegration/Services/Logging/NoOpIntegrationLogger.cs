using Microsoft.Extensions.Logging;

namespace NeroTrade.JDIntegration.Services.Logging;

public sealed class NoOpIntegrationLogger : IIntegrationLogger
{
    private readonly ILogger<NoOpIntegrationLogger> _logger;
    private readonly string _integrationName;

    public NoOpIntegrationLogger(SupabaseOptions options, ILogger<NoOpIntegrationLogger> logger)
    {
        _logger = logger;
        _integrationName = options.IntegrationName;
    }

    public Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
    {
        _logger.Log(
            ResolveLogLevel(entry.Level),
            "[NoOpIntegrationLogger] {Source} {ExternalId} {Message}",
            entry.SourceSystem, entry.ExternalId, entry.Message);
        return Task.CompletedTask;
    }

    public IntegrationRun BeginRun(string runName, CancellationToken cancellationToken) =>
        new(this, _integrationName, runName, cancellationToken);

    public Task MarkResolvedAsync(string integrationName, string externalId, Guid successCorrelationId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[NoOpIntegrationLogger] auto-resolve {Integration} {ExternalId} via {CorrelationId}",
            integrationName, externalId, successCorrelationId);
        return Task.CompletedTask;
    }

    private static LogLevel ResolveLogLevel(string level) => level?.ToLowerInvariant() switch
    {
        "error" => LogLevel.Error,
        "warning" => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}

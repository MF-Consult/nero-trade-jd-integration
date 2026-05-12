using Microsoft.Extensions.Logging;

namespace NeroTrade.JDIntegration.Services.Logging;

public sealed class NoOpIntegrationLogger : IIntegrationLogger
{
    private readonly ILogger<NoOpIntegrationLogger> _logger;

    public NoOpIntegrationLogger(ILogger<NoOpIntegrationLogger> logger)
    {
        _logger = logger;
    }

    public Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
    {
        _logger.Log(
            ResolveLogLevel(entry.Level),
            "[NoOpIntegrationLogger] {Source} {ExternalId} {Message}",
            entry.SourceSystem, entry.ExternalId, entry.Message);
        return Task.CompletedTask;
    }

    private static LogLevel ResolveLogLevel(string level) => level?.ToLowerInvariant() switch
    {
        "error" => LogLevel.Error,
        "warning" => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}

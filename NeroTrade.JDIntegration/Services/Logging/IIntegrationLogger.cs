using System.Text.Json;

namespace NeroTrade.JDIntegration.Services.Logging;

public interface IIntegrationLogger
{
    Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken);
}

public sealed record IntegrationLogEntry(
    string IntegrationName,
    string Level,           // "info" | "warning" | "error"
    string SourceSystem,    // "Uniconta" | "JD" | "Integration"
    string? ExternalId,     // OrderNumber, JD container ID, debtor account, etc.
    string Message,
    string? StackTrace,
    JsonElement? Payload
);

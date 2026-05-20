using System.Text.Json;

namespace NeroTrade.JDIntegration.Services.Logging;

public interface IIntegrationLogger
{
    Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// A structured log row written to <c>public.integration_logs</c> in Supabase.
/// The positional fields are the original surface; the init-only properties below are agent-actionable
/// extensions added in 2026-05 for the monitoring + proactive-remediation pipeline. Existing call sites
/// compile unchanged; new fields are set via object-initializer syntax.
/// </summary>
public sealed record IntegrationLogEntry(
    string IntegrationName,
    string Level,           // "info" | "warning" | "error"
    string SourceSystem,    // "Uniconta" | "JD" | "Integration"
    string? ExternalId,     // OrderNumber, JD container ID, debtor account, etc.
    string Message,
    string? StackTrace,
    JsonElement? Payload)
{
    /// <summary>Stable taxonomy key, e.g. <c>JD_TIMEOUT</c>, <c>UNICONTA_DUPLICATE_SO</c>. SCREAMING_SNAKE.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Shared across every log emitted from a single sync invocation. Set via <see cref="IntegrationLogScope"/>.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>True for transient failures the agent (or a manual retry) can re-attempt safely.</summary>
    public bool? Retryable { get; init; }

    /// <summary>Attempt number within the same correlation chain. 1-based.</summary>
    public int? Attempt { get; init; }

    /// <summary>Free-text hint from the catch block — e.g. "retry after 60s", "manual review: invalid VAT".</summary>
    public string? SuggestedAction { get; init; }
}

using System.Text.Json;

namespace NeroTrade.JDIntegration.Services.Logging;

public interface IIntegrationLogger
{
    /// <summary>
    /// Logical integration name written to <c>integration_name</c> on every row. Exposed here so call
    /// sites don't need to inject the storage-backend options class just to read this string.
    /// </summary>
    string IntegrationName { get; }

    Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a run scope: starts a stopwatch and returns a handle that writes a single "completed" row
    /// on dispose (level=error if <see cref="IntegrationRun.MarkFailed"/> was called or an exception
    /// bubbled). The completion row carries started_at + duration_ms in payload, so an explicit start
    /// event is duplicate noise.
    /// </summary>
    IntegrationRun BeginRun(string runName);

    /// <summary>
    /// Flips any still-open (status in 'open','ack') failure rows for the SAME external_id, project and
    /// integration_name to status='auto_fixed' and records the success log id + correlation id in resolution.
    /// Never touches wontfix/resolved rows.
    /// </summary>
    Task MarkResolvedAsync(
        string integrationName,
        string externalId,
        Guid successCorrelationId,
        CancellationToken cancellationToken);
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

    /// <summary>Set only on the completion row written by <see cref="IntegrationRun"/>. NULL on per-event rows.</summary>
    public int? DurationMs { get; init; }

    /// <summary>Surfaces run_name into payload.run_name on started/completion rows for the integration_runs view.</summary>
    public string? RunName { get; init; }
}

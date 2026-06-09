using System.Diagnostics;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Services.Logging;

/// <summary>
/// Run-scope wrapper for a single sync invocation. Starts a stopwatch at construction and writes a
/// single "completed" row in DisposeAsync — including the case where an exception bubbles — carrying
/// started_at + duration_ms in payload. No separate "started" row is emitted; the completion row alone
/// satisfies the heartbeat, so kørselshistorikken stays one-row-per-run instead of two.
/// </summary>
public sealed class IntegrationRun : IAsyncDisposable
{
    private readonly IIntegrationLogger _logger;
    private readonly string _integrationName;
    private readonly string _runName;
    private readonly Stopwatch _stopwatch;
    private readonly DateTimeOffset _startedAt;

    private Exception? _failure;
    private object? _completionPayload;
    private bool _disposed;

    internal IntegrationRun(
        IIntegrationLogger logger,
        string integrationName,
        string runName)
    {
        _logger = logger;
        _integrationName = integrationName;
        _runName = runName;
        _startedAt = DateTimeOffset.UtcNow;
        _stopwatch = Stopwatch.StartNew();

        Scope = new IntegrationLogScope { RunName = runName };

        // No started row is emitted — the completion row in DisposeAsync already carries
        // started_at + duration_ms in payload, so an explicit start event is duplicate noise.
        // Heartbeat ("did this tick run?") is satisfied by the completion row alone.
    }

    public IntegrationLogScope Scope { get; }
    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

    /// <summary>
    /// Why the run ended — surfaced on the completion row as <c>payload.exit_reason</c>. Lets
    /// dead-window diagnosis distinguish "0 ms tick because nothing was eligible" from
    /// "0 ms tick because a gate short-circuited the flow". Recommended values:
    /// <c>"completed"</c>, <c>"no_eligible_orders"</c>, <c>"inventories_unavailable"</c>,
    /// <c>"skipped_overlap"</c>, <c>"run_failed"</c>. Set explicitly per exit path in the
    /// function body; defaults to <c>"completed"</c> in <see cref="DisposeAsync"/> when the run
    /// did not fail and the caller did not specify, so an unset value still produces a meaningful row.
    /// </summary>
    public string? ExitReason { get; set; }

    /// <summary>
    /// Attach counts/timings that the caller wants surfaced on the completion row. Anonymous object is fine —
    /// shape becomes the completion row's payload.
    /// </summary>
    public void AttachCompletionPayload(object payload) => _completionPayload = payload;

    /// <summary>Records that the run failed; DisposeAsync will emit the completion row at level=error.</summary>
    public void MarkFailed(Exception ex) => _failure = ex;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _stopwatch.Stop();
        var duration = (int)Math.Min(int.MaxValue, _stopwatch.ElapsedMilliseconds);
        var finishedAt = DateTimeOffset.UtcNow;

        var level = _failure is null ? "info" : "error";
        var message = _failure is null
            ? $"{_runName} completed in {duration} ms"
            : $"{_runName} failed after {duration} ms: {LogSanitizer.Describe(_failure)}";

        var classified = _failure is null ? null : ErrorCodeClassifier.Classify(_failure);

        // Default the exit reason so a row never has a null value — easier to filter on in Supabase.
        // Failure-with-unset wins over "completed" for obvious reasons.
        var exitReason = ExitReason ?? (_failure is null ? "completed" : "run_failed");

        // Merge run_name + started/finished into payload alongside whatever the caller attached.
        var payloadObject = new
        {
            run_name = _runName,
            started_at = _startedAt,
            finished_at = finishedAt,
            duration_ms = duration,
            exit_reason = exitReason,
            counts = _completionPayload
        };

        try
        {
            await _logger.LogAsync(new IntegrationLogEntry(
                _integrationName, level, "Integration", null,
                message, null,
                JsonSerializer.SerializeToElement(payloadObject))
            {
                CorrelationId = Scope.CorrelationId,
                RunName = _runName,
                DurationMs = duration,
                ErrorCode = classified?.ErrorCode,
                Retryable = classified?.Retryable,
                SuggestedAction = classified?.SuggestedAction
            }, CancellationToken.None);
        }
        catch
        {
            // Logging must never break the main flow — SupabaseIntegrationLogger already swallows write errors,
            // but defend against the catch-all in case a future logger implementation throws.
        }
    }
}

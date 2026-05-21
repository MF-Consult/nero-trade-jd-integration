using System.Diagnostics;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Services.Logging;

/// <summary>
/// Run-scope wrapper for a single sync invocation. Emits a "started" info row at construction,
/// runs a stopwatch, and writes a paired "completed" row in DisposeAsync — including the case
/// where an exception bubbles, so kørselshistorikken always sees both start and finish + duration_ms.
/// </summary>
public sealed class IntegrationRun : IAsyncDisposable
{
    private readonly IIntegrationLogger _logger;
    private readonly string _integrationName;
    private readonly string _runName;
    private readonly CancellationToken _cancellationToken;
    private readonly Stopwatch _stopwatch;
    private readonly DateTimeOffset _startedAt;

    private Exception? _failure;
    private object? _completionPayload;
    private bool _disposed;

    internal IntegrationRun(
        IIntegrationLogger logger,
        string integrationName,
        string runName,
        CancellationToken cancellationToken)
    {
        _logger = logger;
        _integrationName = integrationName;
        _runName = runName;
        _cancellationToken = cancellationToken;
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

        // Merge run_name + started/finished into payload alongside whatever the caller attached.
        var payloadObject = new
        {
            run_name = _runName,
            started_at = _startedAt,
            finished_at = finishedAt,
            duration_ms = duration,
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

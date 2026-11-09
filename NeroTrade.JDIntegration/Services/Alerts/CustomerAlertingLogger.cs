using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Logging;

namespace NeroTrade.JDIntegration.Services.Alerts;

/// <summary>
/// Decorates the real integration logger: every row is written exactly as before, and rows the customer can act
/// on (see <see cref="CustomerAlertFormatter"/>) additionally trigger one plain-language mail. Mail is strictly
/// best-effort: any failure is swallowed and logged locally, so alerting can never break or delay a sync.
/// De-duplication is in memory, which matches the single-instance deployment (see CLAUDE.md); a restart can at
/// worst send the same mail once more.
/// </summary>
public sealed class CustomerAlertingLogger(
    IIntegrationLogger inner,
    ICustomerAlertSender sender,
    CustomerAlertOptions options,
    ILogger<CustomerAlertingLogger> logger,
    Func<DateTime>? utcNow = null) : IIntegrationLogger
{
    // Process-wide so the dedup survives the per-invocation logger instances.
    private static readonly ConcurrentDictionary<string, DateTime> LastSent = new();
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public string IntegrationName => inner.IntegrationName;

    public async Task LogAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
    {
        await inner.LogAsync(entry, cancellationToken);
        await TryAlertAsync(entry, cancellationToken);
    }

    public IntegrationRun BeginRun(string runName) => inner.BeginRun(runName);

    public Task MarkResolvedAsync(string integrationName, string externalId, Guid successCorrelationId, CancellationToken cancellationToken)
        => inner.MarkResolvedAsync(integrationName, externalId, successCorrelationId, cancellationToken);

    internal static void ResetDedupForTests() => LastSent.Clear();

    private async Task TryAlertAsync(IntegrationLogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.IsComplete) return;
            var alert = CustomerAlertFormatter.TryFormat(entry);
            if (alert is null) return;

            var now = _utcNow();
            var window = TimeSpan.FromHours(Math.Max(1, options.DedupHours));
            if (LastSent.TryGetValue(alert.DedupKey, out var last) && now - last < window) return;

            // Claim the slot before sending so concurrent rows for the same problem cannot double-send.
            LastSent[alert.DedupKey] = now;
            try
            {
                await sender.SendAsync(alert.Subject, alert.Body, cancellationToken);
                logger.LogInformation("Customer alert mailed: {Subject}", alert.Subject);
            }
            catch
            {
                LastSent.TryRemove(alert.DedupKey, out _); // allow a retry on the next occurrence
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Customer alert mail failed; sync is unaffected");
        }
    }
}

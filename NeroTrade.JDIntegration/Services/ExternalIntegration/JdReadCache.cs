namespace NeroTrade.JDIntegration.Services.ExternalIntegration;

using System.Collections.Concurrent;
using NeroTrade.JDIntegration.Models.ExternalIntegration;

/// <summary>
/// Process-wide cache for JD "master" data (addresses, catalog items, container types) that the sync
/// functions read on almost every invocation. <see cref="JdLogisticsService"/> is registered Scoped
/// (a fresh instance per Azure Functions invocation), so without this each invocation would re-download
/// the full lists from JD. Entries are reloaded once they get older than <see cref="Ttl"/>.
/// Registered as a singleton.
/// </summary>
public sealed class JdReadCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    // After a failed refresh we serve the previous (stale) value until this window passes, instead
    // of retrying every 30s timer tick. Keeps JD from being hammered during an outage while still
    // letting the next refresh happen relatively quickly when JD recovers.
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    private readonly Entry<ConcurrentDictionary<string, JdAddress>> _addressesByAtt = new();
    private readonly Entry<ConcurrentDictionary<string, JdCatalogItem>> _itemsBySku = new();
    private readonly Entry<IReadOnlyList<JdContainerType>> _containerTypes = new();
    private readonly Entry<IReadOnlyList<JdInventory>> _inventories = new();

    public Task<ConcurrentDictionary<string, JdAddress>> GetAddressesByAttAsync(
        Func<Task<IReadOnlyList<JdAddress>>> loader, CancellationToken cancellationToken)
        => _addressesByAtt.GetAsync(async () => new ConcurrentDictionary<string, JdAddress>(
            (await loader())
                .GroupBy(a => a.att ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()),
            StringComparer.OrdinalIgnoreCase), Ttl, FailureBackoff, cancellationToken);

    public Task<ConcurrentDictionary<string, JdCatalogItem>> GetItemsBySkuAsync(
        Func<Task<IReadOnlyList<JdCatalogItem>>> loader, CancellationToken cancellationToken)
        => _itemsBySku.GetAsync(async () => new ConcurrentDictionary<string, JdCatalogItem>(
            (await loader())
                .Where(i => !string.IsNullOrWhiteSpace(i.sku))
                .GroupBy(i => i.sku!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()),
            StringComparer.OrdinalIgnoreCase), Ttl, FailureBackoff, cancellationToken);

    public Task<IReadOnlyList<JdContainerType>> GetContainerTypesAsync(
        Func<Task<IReadOnlyList<JdContainerType>>> loader, CancellationToken cancellationToken)
        => _containerTypes.GetAsync(loader, Ttl, FailureBackoff, cancellationToken);

    // Inventories almost never change. Cached so the 30s sales-order tick and the 1-min status
    // tick don't each fire a separate GET /inventories round-trip per run.
    public Task<IReadOnlyList<JdInventory>> GetInventoriesAsync(
        Func<Task<IReadOnlyList<JdInventory>>> loader, CancellationToken cancellationToken)
        => _inventories.GetAsync(loader, Ttl, FailureBackoff, cancellationToken);

    private sealed class Entry<T> where T : class
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private T? _value;
        private DateTime _loadedAtUtc;
        private DateTime _nextRetryAtUtc;

        public async Task<T> GetAsync(Func<Task<T>> loader, TimeSpan ttl, TimeSpan failureBackoff, CancellationToken cancellationToken)
        {
            if (_value != null && DateTime.UtcNow - _loadedAtUtc < ttl)
                return _value;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_value != null && DateTime.UtcNow - _loadedAtUtc < ttl)
                    return _value;

                // The previous refresh attempt failed. Serve the still-cached value rather than
                // hammer the failing endpoint on every 30s tick.
                if (_value != null && DateTime.UtcNow < _nextRetryAtUtc)
                    return _value;

                try
                {
                    var fresh = await loader();
                    _value = fresh;
                    _loadedAtUtc = DateTime.UtcNow;
                    _nextRetryAtUtc = default;
                    return _value;
                }
                catch
                {
                    // Don't poison the cache with an empty/falsely-fresh value just because the
                    // loader threw. Keep the previous value (potentially stale) so the main flow
                    // can continue, and back off before the next refresh attempt.
                    _nextRetryAtUtc = DateTime.UtcNow + failureBackoff;
                    if (_value != null)
                        return _value;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

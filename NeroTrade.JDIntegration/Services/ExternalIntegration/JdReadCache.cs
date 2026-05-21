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
            StringComparer.OrdinalIgnoreCase), Ttl, cancellationToken);

    public Task<ConcurrentDictionary<string, JdCatalogItem>> GetItemsBySkuAsync(
        Func<Task<IReadOnlyList<JdCatalogItem>>> loader, CancellationToken cancellationToken)
        => _itemsBySku.GetAsync(async () => new ConcurrentDictionary<string, JdCatalogItem>(
            (await loader())
                .Where(i => !string.IsNullOrWhiteSpace(i.sku))
                .GroupBy(i => i.sku!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()),
            StringComparer.OrdinalIgnoreCase), Ttl, cancellationToken);

    public Task<IReadOnlyList<JdContainerType>> GetContainerTypesAsync(
        Func<Task<IReadOnlyList<JdContainerType>>> loader, CancellationToken cancellationToken)
        => _containerTypes.GetAsync(loader, Ttl, cancellationToken);

    // Inventories almost never change. Cached so the 30s sales-order tick and the 1-min status
    // tick don't each fire a separate GET /inventories round-trip per run.
    public Task<IReadOnlyList<JdInventory>> GetInventoriesAsync(
        Func<Task<IReadOnlyList<JdInventory>>> loader, CancellationToken cancellationToken)
        => _inventories.GetAsync(loader, Ttl, cancellationToken);

    private sealed class Entry<T> where T : class
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private T? _value;
        private DateTime _loadedAtUtc;

        public async Task<T> GetAsync(Func<Task<T>> loader, TimeSpan ttl, CancellationToken cancellationToken)
        {
            if (_value != null && DateTime.UtcNow - _loadedAtUtc < ttl)
                return _value;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_value != null && DateTime.UtcNow - _loadedAtUtc < ttl)
                    return _value;

                _value = await loader();
                _loadedAtUtc = DateTime.UtcNow;
                return _value;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

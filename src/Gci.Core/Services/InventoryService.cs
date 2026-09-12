using System.Collections.Concurrent;
using Gci.Core.Models;
using Gci.Core.Providers;

namespace Gci.Core.Services;

public sealed record StoreStatus
{
    public required string StoreKey { get; init; }
    public DateTimeOffset? LastAttempt { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }
    public string? Error { get; init; }
    public int ItemCount { get; init; }
    public int InStockCount { get; init; }
}

public sealed record RefreshResult(IReadOnlyList<ChangeEvent> Events, int StoresSucceeded, int StoresFailed, DateTimeOffset FinishedAt);

/// <summary>
/// Owns the store list, the last good snapshot per store, and the change log.
/// A refresh fetches each store, diffs it against its last good snapshot, and persists the result.
/// A failed or suspiciously empty fetch never counts as "everything sold out".
/// </summary>
public sealed class InventoryService : IDisposable
{
    public const string StoresFile = "stores.json";
    public const string StateFile = "state.json";
    public const string ChangesFile = "changes.json";
    public const string ProvidersOverrideFile = "providers.json";
    private const int MaxLoggedChanges = 3000;
    private const int MaxParallelStores = 4;

    private readonly DataStore _data;
    private readonly ProviderHttp _http;
    private readonly Dictionary<ProviderKind, IInventoryProvider> _providers;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _lock = new();

    private List<StoreInfo> _stores;
    private PersistedState _state;
    private List<ChangeEvent> _changes;

    public InventoryService(DataStore data, ProviderConfig? config = null, HttpMessageHandler? handler = null)
    {
        _data = data;
        config ??= ProviderConfig.Load(data.PathFor(ProvidersOverrideFile));
        _http = new ProviderHttp(config.UserAgent, handler);
        _providers = new IInventoryProvider[]
        {
            new TrulieveProvider(_http, config.Trulieve),
            new MosaicProvider(_http, config.Mosaic),
            new JaneProvider(_http, config.Jane),
            new DutchieProvider(_http, config.Dutchie),
            new SweedProvider(_http, config.Sweed),
        }.ToDictionary(p => p.Kind);

        _stores = data.Load(StoresFile, () => new List<StoreInfo>());
        _state = data.Load(StateFile, () => new PersistedState());
        _changes = data.Load(ChangesFile, () => new List<ChangeEvent>());
    }

    public IReadOnlyList<StoreInfo> Stores { get { lock (_lock) return _stores.ToList(); } }

    public IReadOnlyList<ChangeEvent> Changes { get { lock (_lock) return _changes.ToList(); } }

    public StoreStatus? GetStatus(string storeKey)
    {
        lock (_lock) return _state.Status.GetValueOrDefault(storeKey);
    }

    public StoreInfo? GetStore(string storeKey)
    {
        lock (_lock) return _stores.FirstOrDefault(s => s.Key == storeKey);
    }

    /// <summary>Latest known items for the given stores (from the last successful refresh of each).</summary>
    public IReadOnlyList<InventoryItem> GetItems(IEnumerable<string> storeKeys)
    {
        lock (_lock)
            return storeKeys.SelectMany(k => _state.Snapshots.GetValueOrDefault(k) ?? []).ToList();
    }

    /// <summary>Asks every provider for its store list. Keeps previously known stores a provider fails to return.</summary>
    public async Task<IReadOnlyList<string>> DiscoverStoresAsync(CancellationToken ct)
    {
        var errors = new ConcurrentBag<string>();
        var found = await Task.WhenAll(_providers.Values.Select(async p =>
        {
            try
            {
                return (p.Kind, Stores: (IReadOnlyList<StoreInfo>?)await p.DiscoverStoresAsync(ct));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add($"{p.Kind}: {ex.Message}");
                return (p.Kind, Stores: null);
            }
        }));

        lock (_lock)
        {
            var merged = new List<StoreInfo>();
            foreach (var (kind, stores) in found)
            {
                if (stores is { Count: > 0 }) merged.AddRange(stores);
                else merged.AddRange(_stores.Where(s => s.Provider == kind));
            }
            _stores = merged.DistinctBy(s => s.Key).ToList();
            _data.Save(StoresFile, _stores);
        }
        return errors.ToList();
    }

    public async Task<RefreshResult> RefreshAsync(IReadOnlyCollection<string> storeKeys, CancellationToken ct,
        IProgress<(int Done, int Total)>? progress = null)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var targets = Stores.Where(s => storeKeys.Contains(s.Key)).ToList();
            var events = new ConcurrentBag<ChangeEvent>();
            var ok = 0;
            var failed = 0;
            var done = 0;

            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelStores, CancellationToken = ct },
                async (store, token) =>
                {
                    var outcome = await RefreshStoreAsync(store, token);
                    if (outcome is null) Interlocked.Increment(ref failed);
                    else
                    {
                        Interlocked.Increment(ref ok);
                        foreach (var e in outcome) events.Add(e);
                    }
                    progress?.Report((Interlocked.Increment(ref done), targets.Count));
                });

            var ordered = events.OrderBy(e => e.StoreName).ThenBy(e => e.Name).ToList();
            lock (_lock)
            {
                _changes.InsertRange(0, ordered.Where(e => e.Kind != ChangeKind.QuantityChanged));
                if (_changes.Count > MaxLoggedChanges) _changes.RemoveRange(MaxLoggedChanges, _changes.Count - MaxLoggedChanges);
                _data.Save(StateFile, _state);
                _data.Save(ChangesFile, _changes);
            }
            return new RefreshResult(ordered, ok, failed, DateTimeOffset.Now);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <returns>The store's change events, or null when the fetch failed.</returns>
    private async Task<List<ChangeEvent>?> RefreshStoreAsync(StoreInfo store, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        List<InventoryItem> items;
        try
        {
            items = (await _providers[store.Provider].FetchInventoryAsync(store, ct)).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure(store, now, ex.Message);
            return null;
        }

        lock (_lock)
        {
            var previous = _state.Snapshots.GetValueOrDefault(store.Key);
            if (items.Count == 0 && previous is not null && previous.Count(i => i.InStock) > 5)
            {
                RecordFailure(store, now, "Menu came back empty; keeping the previous data.");
                return null;
            }

            if (!_state.EverSeen.TryGetValue(store.Key, out var seen))
                _state.EverSeen[store.Key] = seen = new HashSet<string>();

            var events = InventoryDiffer.Diff(store, previous, items, seen, now);
            _state.Snapshots[store.Key] = items;
            _state.Status[store.Key] = new StoreStatus
            {
                StoreKey = store.Key,
                LastAttempt = now,
                LastSuccess = now,
                ItemCount = items.Count,
                InStockCount = items.Count(i => i.InStock),
            };
            return events;
        }
    }

    private void RecordFailure(StoreInfo store, DateTimeOffset now, string error)
    {
        lock (_lock)
        {
            var prior = _state.Status.GetValueOrDefault(store.Key);
            _state.Status[store.Key] = new StoreStatus
            {
                StoreKey = store.Key,
                LastAttempt = now,
                LastSuccess = prior?.LastSuccess,
                Error = error,
                ItemCount = prior?.ItemCount ?? 0,
                InStockCount = prior?.InStockCount ?? 0,
            };
        }
    }

    public void ClearChanges()
    {
        lock (_lock)
        {
            _changes.Clear();
            _data.Save(ChangesFile, _changes);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }

    internal sealed class PersistedState
    {
        public Dictionary<string, List<InventoryItem>> Snapshots { get; set; } = new();
        public Dictionary<string, HashSet<string>> EverSeen { get; set; } = new();
        public Dictionary<string, StoreStatus> Status { get; set; } = new();
    }
}

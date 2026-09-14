using Gci.Core.Models;

namespace Gci.Core.Services;

/// <summary>Compares two refreshes of the same store and reports what changed.</summary>
public static class InventoryDiffer
{
    /// <summary>A count increase must be at least this many units...</summary>
    public const int RestockMinUnits = 5;
    /// <summary>...and at least this fraction of the previous count to be reported as a restock.</summary>
    public const decimal RestockMinFraction = 0.25m;

    /// <param name="previous">Last successful refresh, or null when this is the first one (baseline: no events).</param>
    /// <param name="current">Items from this refresh.</param>
    /// <param name="everSeen">Item keys ever observed at the store; used to tell "new" from "back in stock". Updated in place.</param>
    public static List<ChangeEvent> Diff(
        StoreInfo store,
        IReadOnlyCollection<InventoryItem>? previous,
        IReadOnlyCollection<InventoryItem> current,
        ISet<string> everSeen,
        DateTimeOffset now)
    {
        var events = new List<ChangeEvent>();
        var before = previous?.Where(i => i.InStock).ToDictionary(i => i.Key) ?? new();
        var after = current.Where(i => i.InStock).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());

        if (previous is not null)
        {
            foreach (var (key, item) in after)
            {
                if (!before.TryGetValue(key, out var old))
                {
                    var kind = everSeen.Contains(key) ? ChangeKind.BackInStock : ChangeKind.NewProduct;
                    events.Add(Make(kind, store, item, now, newPrice: item.EffectivePrice, newQty: item.Quantity));
                    continue;
                }

                if (old.EffectivePrice is { } op && item.EffectivePrice is { } np && op != np)
                {
                    var kind = np < op ? ChangeKind.PriceDrop : ChangeKind.PriceIncrease;
                    events.Add(Make(kind, store, item, now, op, np, old.Quantity, item.Quantity));
                }

                var arrived = old.Batches is null || item.Batches is null
                    ? new List<string>()
                    : item.Batches.Keys.Except(old.Batches.Keys).ToList();
                var jumped = old.Quantity is { } before0 && item.Quantity is { } after0
                    && after0 - before0 >= Math.Max(RestockMinUnits, before0 * RestockMinFraction);
                if (arrived.Count > 0 || jumped)
                {
                    var detail = arrived.Count > 0
                        ? "New batch: " + string.Join(", ", arrived.Select(b => item.Batches![b]).Distinct())
                        : null;
                    events.Add(Make(ChangeKind.Restocked, store, item, now, old.EffectivePrice, item.EffectivePrice,
                        old.Quantity, item.Quantity, detail));
                }

                if (old.Quantity is { } oq && item.Quantity is { } nq && oq != nq)
                    events.Add(Make(ChangeKind.QuantityChanged, store, item, now, old.EffectivePrice, item.EffectivePrice, oq, nq));
            }

            foreach (var (key, old) in before)
            {
                if (!after.ContainsKey(key))
                    events.Add(Make(ChangeKind.SoldOut, store, old, now, old.EffectivePrice, null, old.Quantity, 0));
            }
        }

        foreach (var item in current)
            everSeen.Add(item.Key);

        return events;
    }

    private static ChangeEvent Make(ChangeKind kind, StoreInfo store, InventoryItem item, DateTimeOffset now,
        decimal? oldPrice = null, decimal? newPrice = null, int? oldQty = null, int? newQty = null, string? detail = null) => new()
    {
        Detail = detail,
        At = now,
        Kind = kind,
        StoreKey = store.Key,
        StoreName = store.DisplayName,
        Operator = store.Operator,
        ItemKey = item.Key,
        Name = item.Name,
        Brand = item.Brand,
        Category = item.Category,
        RawCategory = item.RawCategory,
        Size = item.Size,
        OldPrice = oldPrice,
        NewPrice = newPrice,
        OldQuantity = oldQty,
        NewQuantity = newQty,
        Url = item.Url,
        ImageUrl = item.ImageUrl,
        ThumbnailUrl = item.ThumbnailUrl,
    };
}

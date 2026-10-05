using Gci.Core.Models;

namespace Gci.Core.Services;

/// <summary>What an item's price history says about today's price.</summary>
/// <param name="Usual">The price it has held longest in the window (a time-weighted mode, so brief dips don't move it).</param>
/// <param name="Lowest30">Today's price is the lowest of the last 30 days, and it was dearer at some point in them.</param>
/// <param name="Below">Today's price is meaningfully under <paramref name="Usual"/> (at least $2 and 5%).</param>
/// <param name="Above">Today's price is meaningfully over <paramref name="Usual"/>.</param>
/// <param name="Percent">How far today's price is from <paramref name="Usual"/>, as a whole percentage.</param>
public sealed record PriceStats(decimal Usual, decimal Low30, decimal Low, decimal High, bool Lowest30, bool Below, bool Above, int Percent,
    IReadOnlyList<PriceHistory.PricePoint> Changes);

/// <summary>
/// Every price change GCI has seen per item (from PriceDrop / PriceIncrease events), kept for <see cref="Retention"/> in
/// its own file — the change log is capped at a few thousand events of every kind, far too short for "usually $X".
/// The first load seeds from that change log. Same rules as the web app's price history.
/// </summary>
public sealed class PriceHistory
{
    public const string FileName = "price-history.json";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);
    /// <summary>A price that lasted less than this is treated as a menu glitch (they flicker and revert).</summary>
    public static readonly TimeSpan Blip = TimeSpan.FromHours(1);

    public sealed record PricePoint(DateTimeOffset At, decimal Old, decimal New);

    /// <summary>When recording began: a window never reaches back before what GCI actually observed.</summary>
    public DateTimeOffset Since { get; set; }
    public Dictionary<string, List<PricePoint>> Items { get; set; } = new();

    public static PriceHistory Load(DataStore data, IEnumerable<ChangeEvent> seedFrom, DateTimeOffset now)
    {
        if (File.Exists(data.PathFor(FileName)))
        {
            var loaded = data.Load(FileName, () => new PriceHistory { Since = now });
            if (loaded.Since == default) loaded.Since = now;
            return loaded;
        }
        var seed = seedFrom.ToList();
        var history = new PriceHistory { Since = seed.Count > 0 ? seed.Min(e => e.At) : now };
        history.Record(seed);
        history.Prune(now);
        return history;
    }

    /// <summary>Adds the price changes among <paramref name="events"/>; true when anything was added.</summary>
    public bool Record(IEnumerable<ChangeEvent> events)
    {
        var added = false;
        foreach (var e in events.Where(e => e.Kind is ChangeKind.PriceDrop or ChangeKind.PriceIncrease).OrderBy(e => e.At))
        {
            if (e.OldPrice is not { } old || e.NewPrice is not { } @new || old == @new) continue;
            if (!Items.TryGetValue(e.ItemKey, out var list)) Items[e.ItemKey] = list = new();
            if (list.Any(p => p.At == e.At && p.New == @new)) continue; // already recorded (seeding overlaps)
            list.Add(new PricePoint(e.At, old, @new));
            list.Sort((a, b) => a.At.CompareTo(b.At));
            added = true;
        }
        return added;
    }

    public void Prune(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        foreach (var key in Items.Keys.ToList())
        {
            Items[key].RemoveAll(p => p.At < cutoff);
            if (Items[key].Count == 0) Items.Remove(key);
        }
    }

    public void Save(DataStore data) => data.Save(FileName, this);

    /// <summary>Null when the item's price hasn't really moved in the window (no history, or only glitches).</summary>
    public PriceStats? StatsFor(string itemKey, decimal? currentPrice, DateTimeOffset now, int windowDays = 90)
    {
        if (currentPrice is not { } price || !Items.TryGetValue(itemKey, out var points) || points.Count == 0) return null;

        // Price over time as segments [start, end, price] across the window, ending with today's live price.
        var start = Max(now.AddDays(-windowDays), Since);
        var raw = new List<(DateTimeOffset From, DateTimeOffset To, decimal Price)>();
        var t0 = start;
        var p = points[0].Old;
        foreach (var pt in points)
        {
            if (pt.At > t0) raw.Add((t0, pt.At, p));
            t0 = Max(t0, pt.At);
            p = pt.New;
        }
        raw.Add((t0, now, price));

        // Drop blips (the live segment always stays), then merge equal neighbours.
        var segs = new List<(DateTimeOffset From, DateTimeOffset To, decimal Price)>();
        for (var i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            if (s.To <= s.From || (i < raw.Count - 1 && s.To - s.From < Blip)) continue;
            if (segs.Count > 0 && segs[^1].Price == s.Price) segs[^1] = (segs[^1].From, s.To, s.Price);
            else if (segs.Count > 0) segs.Add((segs[^1].To, s.To, s.Price));
            else segs.Add(s);
        }
        if (segs.Count < 2) return null;

        var since30 = now.AddDays(-30);
        var durations = new Dictionary<decimal, TimeSpan>();
        decimal low30 = decimal.MaxValue, low = decimal.MaxValue, high = decimal.MinValue;
        var dearer30 = false;
        foreach (var s in segs)
        {
            durations[s.Price] = durations.GetValueOrDefault(s.Price) + (s.To - s.From);
            low = Math.Min(low, s.Price);
            high = Math.Max(high, s.Price);
            if (s.To > since30)
            {
                low30 = Math.Min(low30, s.Price);
                if (s.Price > price) dearer30 = true;
            }
        }
        var usual = durations.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        var gap = price - usual;
        var big = Math.Abs(gap) >= Math.Max(2m, usual * 0.05m);
        var pct = usual > 0 ? (int)Math.Round(Math.Abs(gap) / usual * 100, MidpointRounding.AwayFromZero) : 0; // as the web app rounds
        return new PriceStats(usual, low30, low, high, price <= low30 && dearer30, big && gap < 0, big && gap > 0, pct,
            points.Where(x => x.At >= start).ToList());
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

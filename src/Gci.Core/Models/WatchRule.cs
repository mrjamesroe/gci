using System.Text.Json.Serialization;

namespace Gci.Core.Models;

/// <summary>
/// A user-defined filter over change events. An event notifies when it satisfies every
/// populated criterion and its kind is one the rule is listening for.
/// </summary>
public sealed class WatchRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New watch";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Any-of list. Each entry may hold several words, all of which must appear
    /// (e.g. "live rosin" matches "Blue Dream Live Rosin 1g" but not "Live Resin").
    /// Matched against product name, brand, strain, and provider category.
    /// </summary>
    public List<string> Keywords { get; set; } = new();
    public ProductCategory? Category { get; set; }
    public string? Operator { get; set; }
    /// <summary>Store keys to limit to; empty means every monitored store.</summary>
    public List<string> StoreKeys { get; set; } = new();
    public decimal? MaxPrice { get; set; }

    public bool NotifyAvailable { get; set; } = true;
    /// <summary>A new batch arrived or the count jumped while the item was already listed.</summary>
    public bool NotifyRestock { get; set; } = true;
    public bool NotifySoldOut { get; set; }
    public bool NotifyPriceDrop { get; set; } = true;
    /// <summary>Notify when a count drops to or below this many units.</summary>
    public int? LowStockThreshold { get; set; }

    [JsonIgnore]
    public string Summary => Describe();

    /// <param name="storeName">Resolves a store key to a display name; without it, stores are just counted.</param>
    public string Describe(Func<string, string?>? storeName = null)
    {
        var parts = new List<string>();
        if (Keywords.Count > 0) parts.Add(string.Join(" or ", Keywords.Select(k => $"\"{k}\"")));
        if (Category is { } c) parts.Add(c.ToString());
        if (!string.IsNullOrWhiteSpace(Operator)) parts.Add(Operator!);
        if (StoreKeys.Count > 0)
        {
            var names = storeName is null ? [] : StoreKeys.Select(storeName).OfType<string>().ToList();
            parts.Add(names.Count is > 0 and <= 2 && names.Count == StoreKeys.Count
                ? "at " + string.Join(", ", names)
                : StoreKeys.Count == 1 ? "1 store" : $"{StoreKeys.Count} stores");
        }
        if (MaxPrice is { } p) parts.Add($"≤ {p:C0}");
        return parts.Count == 0 ? "Everything" : string.Join(" · ", parts);
    }
}

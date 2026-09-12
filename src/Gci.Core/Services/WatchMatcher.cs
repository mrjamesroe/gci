using Gci.Core.Models;

namespace Gci.Core.Services;

public static class WatchMatcher
{
    /// <summary>True when the item satisfies the rule's filters (ignoring which event kinds it listens for).</summary>
    public static bool Matches(WatchRule rule, InventoryItem item, StoreInfo store) =>
        MatchesFilters(rule, store.Key, store.Operator, item.Category, item.EffectivePrice,
            item.Name, item.Brand, item.Strain, item.RawCategory);

    /// <summary>True when the rule wants to be notified about this event.</summary>
    public static bool Matches(WatchRule rule, ChangeEvent e)
    {
        if (!rule.Enabled) return false;

        var wanted = e.Kind switch
        {
            ChangeKind.NewProduct or ChangeKind.BackInStock => rule.NotifyAvailable,
            ChangeKind.Restocked => rule.NotifyRestock,
            ChangeKind.SoldOut => rule.NotifySoldOut,
            ChangeKind.PriceDrop => rule.NotifyPriceDrop,
            ChangeKind.QuantityChanged => rule.LowStockThreshold is { } t
                && e.OldQuantity > t && e.NewQuantity <= t && e.NewQuantity > 0,
            _ => false,
        };
        if (!wanted) return false;

        var price = e.NewPrice ?? e.OldPrice;
        return MatchesFilters(rule, e.StoreKey, e.Operator, e.Category, price, e.Name, e.Brand, null, e.RawCategory);
    }

    /// <summary>
    /// True when a news post mentions what the rule watches for: any of its keywords (plus its operator, if set)
    /// appears in the title, summary or tags. Rules without keywords never match posts.
    /// </summary>
    public static bool Matches(WatchRule rule, FeedPost post)
    {
        if (!rule.Enabled || rule.Keywords.Count == 0) return false;
        var text = $"{post.Title} {post.Summary} {string.Join(' ', post.Categories)}";
        if (!string.IsNullOrWhiteSpace(rule.Operator) && !text.Contains(rule.Operator!, StringComparison.OrdinalIgnoreCase)) return false;
        return rule.Keywords.Any(k => KeywordMatches(k, text));
    }

    private static bool MatchesFilters(WatchRule rule, string storeKey, string op, ProductCategory category,
        decimal? price, string name, string? brand, string? strain, string? rawCategory)
    {
        if (rule.StoreKeys.Count > 0 && !rule.StoreKeys.Contains(storeKey)) return false;
        if (!string.IsNullOrWhiteSpace(rule.Operator) && !string.Equals(rule.Operator, op, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.Category is { } c && c != category) return false;
        if (rule.MaxPrice is { } max && price is { } p && p > max) return false;

        if (rule.Keywords.Count == 0) return true;
        var haystack = $"{name} {brand} {strain} {rawCategory}";
        return rule.Keywords.Any(k => KeywordMatches(k, haystack));
    }

    private static bool KeywordMatches(string keyword, string haystack)
    {
        var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length > 0 && words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}

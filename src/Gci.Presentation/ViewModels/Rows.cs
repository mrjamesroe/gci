using CommunityToolkit.Mvvm.ComponentModel;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.App.ViewModels;

public sealed class ItemRow(InventoryItem item, StoreInfo store, bool watched)
{
    public InventoryItem Item { get; } = item;
    public ImageRef? Image { get; } = item.ImageUrl is { } url ? new ImageRef(item.Key, url, item.ThumbnailUrl) : null;
    public StoreInfo Store { get; } = store;
    public bool Watched { get; } = watched;
    public string WatchMark => Watched ? "★" : "";
    public string Category => Item.Category.ToString();
    public string Name => Item.Name;
    public string? Brand => Item.Brand;
    public string? Size => Item.Size;
    public string? Potency => Item.Potency;
    public string? Strain => Item.Strain;
    public decimal? Price => Item.EffectivePrice;
    public string PriceText => Item.SalePrice is { } sale
        ? $"{sale:C0} (was {Item.Price:C0})"
        : Item.Price?.ToString("C0") ?? "";
    public bool OnSale => Item.SalePrice is not null;
    /// <summary>A short promo label for awareness: the provider's named promo (e.g. "BOGO 50% Off"), or a
    /// computed "N% off" when the item is simply discounted. Null when there's no promo.</summary>
    public string? PromoText => Item.Promo ?? (Item.SalePrice is { } sale && Item.Price is { } p && p > 0 && sale < p
        ? $"{Math.Round((1 - sale / p) * 100)}% off" : null);
    public int? Quantity => Item.Quantity;
    public string QuantityText => Item.Quantity is { } q
        ? (Item.QuantityAtLeast ? $"{q}+" : q.ToString())
        : (Item.InStock ? "✓" : "0");
    public bool InStock => Item.InStock;
    public string StoreName => Store.DisplayName;
    public string Operator => Store.Operator;
    public string? City => Store.City;
    public string? Url => Item.Url;
}

public sealed partial class StoreRow(StoreInfo store, bool enabled, Action<StoreRow> onToggled) : ObservableObject
{
    public StoreInfo Store { get; } = store;
    public string Operator => Store.Operator;
    public string Name => Store.Name;
    public string Kind => Store.IsPharmacyPartner ? "Partner pharmacy" : "Dispensary";
    public string? City => Store.City;
    public string? Phone => Store.Phone;
    public string Platform => Store.Provider.ToString();

    [ObservableProperty] private bool _isEnabled = enabled;
    [ObservableProperty] private string _statusText = "Not refreshed yet";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private int _inStockCount;
    [ObservableProperty] private DateTimeOffset? _lastSuccess;

    partial void OnIsEnabledChanged(bool value) => onToggled(this);

    public void Update(StoreStatus? status)
    {
        if (status is null)
        {
            StatusText = IsEnabled ? "Waiting for first refresh" : "Not monitored";
            return;
        }
        Error = status.Error;
        InStockCount = status.InStockCount;
        LastSuccess = status.LastSuccess;
        StatusText = status.Error is not null
            ? $"Error: {status.Error}"
            : $"OK · {status.InStockCount} in stock";
    }
}

public sealed partial class WatchRow(WatchRule rule, Func<string, string?> storeName, Action<WatchRow> onToggled) : ObservableObject
{
    public WatchRule Rule { get; } = rule;
    public string Name => Rule.Name;
    public string Summary => Rule.Describe(storeName);

    public string EventsText
    {
        get
        {
            var parts = new List<string>();
            if (Rule.NotifyAvailable) parts.Add("new/back");
            if (Rule.NotifyRestock) parts.Add("restock");
            if (Rule.NotifyPriceDrop) parts.Add("price drop");
            if (Rule.NotifySoldOut) parts.Add("sold out");
            if (Rule.LowStockThreshold is { } t) parts.Add($"≤{t} left");
            return string.Join(", ", parts);
        }
    }

    [ObservableProperty] private bool _enabled = rule.Enabled;
    [ObservableProperty] private int _matchingNow;

    partial void OnEnabledChanged(bool value)
    {
        Rule.Enabled = value;
        onToggled(this);
    }
}

public sealed class ChangeRow(ChangeEvent e, bool watched, ImageRef? fallbackImage = null)
{
    public ChangeEvent Event { get; } = e;
    // Newer change events carry the image; older ones (recorded before that) fall back to the product's current
    // inventory image when it's still listed.
    public ImageRef? Image { get; } = e.ImageUrl is { } url ? new ImageRef(e.ItemKey, url, e.ThumbnailUrl) : fallbackImage;
    public bool Watched { get; } = watched;
    public string WatchMark => Watched ? "★" : "";
    public DateTimeOffset At => Event.At;
    public string When => Event.At.LocalDateTime.ToString("MMM d h:mm tt");
    public string Kind => Event.Kind switch
    {
        ChangeKind.NewProduct => "New",
        ChangeKind.BackInStock => "Back in stock",
        ChangeKind.Restocked => "Restocked",
        ChangeKind.SoldOut => "Sold out",
        ChangeKind.PriceDrop => "Price drop",
        ChangeKind.PriceIncrease => "Price up",
        _ => Event.Kind.ToString(),
    };
    public string Category => Event.Category.ToString();
    public string Name => Event.Name;
    public string? Size => Event.Size;
    public string StoreName => Event.StoreName;
    public string PriceText => Event.Kind is ChangeKind.PriceDrop or ChangeKind.PriceIncrease
        ? $"{Event.OldPrice:C0} → {Event.NewPrice:C0}"
        : (Event.NewPrice ?? Event.OldPrice)?.ToString("C0") ?? "";
    public string QuantityText => Event.Kind == ChangeKind.Restocked && Event.OldQuantity is { } o && Event.NewQuantity is { } n && n > o
        ? $"{o} → {n}"
        : Event.NewQuantity?.ToString() ?? "";
    public string? Detail => Event.Detail;
    public string? Url => Event.Url;
}

public sealed partial class NewsRow(FeedPost post, string source, bool unread, string? matchedWatch) : ObservableObject
{
    public FeedPost Post { get; } = post;
    public ImageRef? Image { get; } = post.ImageUrl is { } url ? new ImageRef($"feed|{post.Key}", url, post.ThumbnailUrl) : null;
    public string Title => Post.Title;
    public string? Summary => Post.Summary;
    public string? Link => Post.Link;
    public string? MatchedWatch { get; } = matchedWatch;
    public bool IsMatch => MatchedWatch is not null;
    public string MatchText => $"★ {MatchedWatch}";

    public string Meta => string.Join(" · ", new[]
    {
        source,
        Post.PublishedAt?.LocalDateTime.ToString("MMM d, yyyy"),
        Post.Categories.Count > 0 ? string.Join(", ", Post.Categories.Take(3)) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty] private bool _isUnread = unread;
}

public sealed partial class FeedRow : ObservableObject
{
    private readonly Action<FeedRow> _changed;

    public FeedRow(FeedSource source, FeedStatus? status, Action<FeedRow> changed)
    {
        Source = source;
        _changed = changed;
#pragma warning disable MVVMTK0034 // initial values shouldn't trigger a save
        _enabled = source.Enabled;
        _notify = source.Notify;
#pragma warning restore MVVMTK0034
        StatusText = status switch
        {
            null => "Not checked yet",
            { Error: { } err } => $"Error: {err}",
            { LastSuccess: { } ok } => $"Checked {ok.LocalDateTime:MMM d h:mm tt}",
            _ => "",
        };
        HasError = status?.Error is not null;
    }

    public FeedSource Source { get; }
    public string Name => Source.Name;
    public string Url => Source.Url;
    public string StatusText { get; }
    public bool HasError { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _notify;

    partial void OnEnabledChanged(bool value) => _changed(this);
    partial void OnNotifyChanged(bool value) => _changed(this);
}

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

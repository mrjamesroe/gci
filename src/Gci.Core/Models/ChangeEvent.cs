namespace Gci.Core.Models;

public enum ChangeKind
{
    /// <summary>Never seen at this store before.</summary>
    NewProduct,
    /// <summary>Seen before, was unavailable, is available again.</summary>
    BackInStock,
    /// <summary>Still available, but a new batch arrived or the unit count jumped.</summary>
    Restocked,
    SoldOut,
    PriceDrop,
    PriceIncrease,
    /// <summary>Unit count moved; only used for low-stock watch thresholds, never logged.</summary>
    QuantityChanged,
}

/// <summary>A detected change to one item between two refreshes of a store.</summary>
public sealed record ChangeEvent
{
    public required DateTimeOffset At { get; init; }
    public required ChangeKind Kind { get; init; }
    public required string StoreKey { get; init; }
    public required string StoreName { get; init; }
    public required string Operator { get; init; }
    public required string ItemKey { get; init; }
    public required string Name { get; init; }
    public string? Brand { get; init; }
    public ProductCategory Category { get; init; }
    public string? RawCategory { get; init; }
    public string? Size { get; init; }
    public decimal? OldPrice { get; init; }
    public decimal? NewPrice { get; init; }
    public int? OldQuantity { get; init; }
    public int? NewQuantity { get; init; }
    public string? Url { get; init; }
    public string? ImageUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    /// <summary>Extra context, e.g. the potency of a newly arrived batch.</summary>
    public string? Detail { get; init; }

    public string Describe() => Kind switch
    {
        ChangeKind.NewProduct => "New",
        ChangeKind.BackInStock => "Back in stock",
        ChangeKind.Restocked when OldQuantity is { } o && NewQuantity is { } n && n > o => $"Restocked {o} → {n}",
        ChangeKind.Restocked => "Restocked",
        ChangeKind.SoldOut => "Sold out",
        ChangeKind.PriceDrop => $"Price drop {OldPrice:C0} → {NewPrice:C0}",
        ChangeKind.PriceIncrease => $"Price up {OldPrice:C0} → {NewPrice:C0}",
        ChangeKind.QuantityChanged => $"Qty {OldQuantity} → {NewQuantity}",
        _ => Kind.ToString(),
    };
}

using System.Text.Json.Serialization;

namespace Gci.Core.Models;

/// <summary>Normalized product categories shared across every provider.</summary>
public enum ProductCategory
{
    Flower,
    Vape,
    Concentrate,
    Edible,
    Sublingual,
    Capsule,
    Oral,
    Topical,
    Accessory,
    Apparel,
    Other,
}

/// <summary>One purchasable product (or product size) at one store.</summary>
public sealed record InventoryItem
{
    public required string StoreKey { get; init; }
    public required string ProductId { get; init; }
    /// <summary>Size/option identifier; "default" when the product has a single option.</summary>
    public required string VariantId { get; init; }
    public required string Name { get; init; }
    public string? Brand { get; init; }
    public ProductCategory Category { get; init; }
    /// <summary>The provider's own category label, kept for searching and display.</summary>
    public string? RawCategory { get; init; }
    /// <summary>Indica / Sativa / Hybrid when the provider reports it.</summary>
    public string? Strain { get; init; }
    public string? Size { get; init; }
    public decimal? Price { get; init; }
    /// <summary>Discounted price when a sale applies.</summary>
    public decimal? SalePrice { get; init; }
    /// <summary>Units available; null when the provider only reports in/out of stock.</summary>
    public int? Quantity { get; init; }
    /// <summary>True when <see cref="Quantity"/> is a clamped lower bound (e.g. Jane caps its per-order limit),
    /// so the real count is "that many or more". The UI shows it as "N+".</summary>
    public bool QuantityAtLeast { get; init; }
    public bool InStock { get; init; }
    public string? Potency { get; init; }
    /// <summary>In-stock production batches, keyed by batch code, valued by a short label (e.g. "THC 84%").</summary>
    public Dictionary<string, string>? Batches { get; init; }
    public string? Url { get; init; }
    /// <summary>The provider's original product image; used to detect when the picture changes.</summary>
    public string? ImageUrl { get; init; }
    /// <summary>A smaller variant served by the provider's resizing CDN, when one exists.</summary>
    public string? ThumbnailUrl { get; init; }

    [JsonIgnore]
    public string Key => $"{StoreKey}|{ProductId}|{VariantId}";

    [JsonIgnore]
    public decimal? EffectivePrice => SalePrice ?? Price;
}

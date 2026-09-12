using System.Text.Json.Serialization;

namespace Gci.Core.Models;

/// <summary>The menu platform a store's inventory is read from.</summary>
public enum ProviderKind
{
    Trulieve,
    Mosaic,
    Jane,
    Dutchie,
    Sweed,
}

/// <summary>A single dispensary or partner pharmacy that publishes a menu.</summary>
public sealed record StoreInfo
{
    /// <summary>Stable app-wide key, e.g. "trulieve:marietta" or "jane:6135".</summary>
    public required string Key { get; init; }
    public required ProviderKind Provider { get; init; }
    /// <summary>The identifier the provider's API expects (store code, UUID, numeric id, cName).</summary>
    public required string ProviderStoreId { get; init; }
    /// <summary>The licensed operator whose products the store sells, e.g. "Trulieve".</summary>
    public required string Operator { get; init; }
    /// <summary>Location name, e.g. "Marietta" or "Lotus Farmacy - Suwanee".</summary>
    public required string Name { get; init; }
    public string? City { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
    /// <summary>Human-facing menu page for the store.</summary>
    public string? MenuUrl { get; init; }
    /// <summary>Provider-specific slug used to build product links.</summary>
    public string? Slug { get; init; }
    /// <summary>Independent pharmacy carrying an operator's products (rather than an operator-run dispensary).</summary>
    public bool IsPharmacyPartner { get; init; }

    [JsonIgnore]
    public string DisplayName => IsPharmacyPartner ? $"{Name} ({Operator})" : $"{Operator} {Name}";
}

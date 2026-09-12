using System.Reflection;
using System.Text.Json;

namespace Gci.Core.Providers;

/// <summary>
/// Endpoints, keys and store ids for each menu platform. Ships as an embedded default;
/// a providers.json in the data folder overrides it so a changed endpoint can be fixed without a rebuild.
/// </summary>
public sealed class ProviderConfig
{
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0 Safari/537.36 GCI/1.0";

    public TrulieveConfig Trulieve { get; set; } = new();
    public MosaicConfig Mosaic { get; set; } = new();
    public JaneConfig Jane { get; set; } = new();
    public DutchieConfig Dutchie { get; set; } = new();
    public SweedConfig Sweed { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static ProviderConfig LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Gci.Core.providers.default.json")
            ?? throw new InvalidOperationException("Embedded providers.default.json is missing.");
        return JsonSerializer.Deserialize<ProviderConfig>(stream, JsonOptions)!;
    }

    /// <summary>Loads the override file when present, otherwise the embedded default.</summary>
    public static ProviderConfig Load(string? overridePath)
    {
        if (overridePath is not null && File.Exists(overridePath))
            return JsonSerializer.Deserialize<ProviderConfig>(File.ReadAllText(overridePath), JsonOptions)!;
        return LoadDefault();
    }
}

public sealed class TrulieveConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://www.trulieve.com";
    public string StateSlug { get; set; } = "georgia";
    public int PageSize { get; set; } = 100;
    /// <summary>Used when the dispensary list page can't be read.</summary>
    public List<SeedStore> FallbackStores { get; set; } = new();
}

public sealed class MosaicConfig
{
    public bool Enabled { get; set; } = true;
    public string Operator { get; set; } = "Botanical Sciences";
    public string ApiBase { get; set; } = "https://api.mosaic.green/v1.0";
    public string ShopBase { get; set; } = "https://shop.botanicalsciences.com";
    public string CompanyId { get; set; } = "";
}

public sealed class JaneConfig
{
    public bool Enabled { get; set; } = true;
    public string AlgoliaAppId { get; set; } = "";
    public string AlgoliaApiKey { get; set; } = "";
    public string AlgoliaIndex { get; set; } = "menu-products-production";
    public string StoreApi { get; set; } = "https://api.iheartjane.com/v1/stores";
    public List<SeedStore> Stores { get; set; } = new();
}

public sealed class DutchieConfig
{
    public bool Enabled { get; set; } = true;
    public string GraphqlUrl { get; set; } = "https://dutchie.com/api-2/graphql";
    public string DispensaryGraphqlUrl { get; set; } = "https://dutchie.com/graphql";
    /// <summary>Persisted-query hashes captured from dutchie.com; update here if Dutchie rotates them.</summary>
    public string ConsumerDispensariesHash { get; set; } = "";
    public string FilteredProductsHash { get; set; } = "";
    public List<SeedStore> Stores { get; set; } = new();
}

public sealed class SweedConfig
{
    public bool Enabled { get; set; } = true;
    public string ApiBase { get; set; } = "https://web-ui-prime.sweedpos.com/_api/proxy";
    public List<SeedStore> Stores { get; set; } = new();
}

/// <summary>A store known ahead of time (ids for platforms without a store directory).</summary>
public sealed class SeedStore
{
    public string Id { get; set; } = "";
    /// <summary>Secondary id some APIs need (Dutchie's dispensary id behind a cName).</summary>
    public string? InternalId { get; set; }
    public string Operator { get; set; } = "";
    public string? Name { get; set; }
    public string? City { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    /// <summary>Storefront base URL (Sweed) or menu URL.</summary>
    public string? ShopUrl { get; set; }
}

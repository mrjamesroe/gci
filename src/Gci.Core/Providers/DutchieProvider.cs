using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Providers;

/// <summary>
/// Dutchie powers True Bliss. Uses Dutchie's persisted GraphQL queries (hashes in config);
/// unit counts come from the POS metadata attached to each option.
/// </summary>
public sealed class DutchieProvider(ProviderHttp http, DutchieConfig config) : IInventoryProvider
{
    public ProviderKind Kind => ProviderKind.Dutchie;

    public async Task<IReadOnlyList<StoreInfo>> DiscoverStoresAsync(CancellationToken ct)
    {
        if (!config.Enabled) return [];
        var stores = new List<StoreInfo>();
        foreach (var seed in config.Stores)
        {
            JsonNode? d = null;
            try
            {
                var res = await QueryAsync(config.DispensaryGraphqlUrl, "ConsumerDispensaries",
                    new { dispensaryFilter = new { cNameOrID = seed.Id } }, config.ConsumerDispensariesHash, ct);
                d = res["data"]?["filteredDispensaries"]?.AsArray().FirstOrDefault();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Fall back to the seed's internal id.
            }

            var id = d.Str("id") ?? seed.InternalId;
            if (id is null) continue;
            stores.Add(new StoreInfo
            {
                Key = $"dutchie:{seed.Id}",
                Provider = ProviderKind.Dutchie,
                ProviderStoreId = id,
                Operator = seed.Operator,
                Name = seed.Name ?? d.Str("name") ?? seed.Id,
                City = seed.City,
                Address = d.Str("address") ?? seed.Address,
                Phone = d.Str("phone") ?? seed.Phone,
                Slug = seed.Id,
                MenuUrl = seed.ShopUrl ?? $"https://dutchie.com/dispensary/{seed.Id}",
            });
        }
        return stores;
    }

    public async Task<IReadOnlyList<InventoryItem>> FetchInventoryAsync(StoreInfo store, CancellationToken ct)
    {
        var items = new List<InventoryItem>();
        for (var page = 0; page < 20; page++)
        {
            var variables = new
            {
                includeEnterpriseSpecials = false,
                productsFilter = new
                {
                    productIds = Array.Empty<string>(),
                    dispensaryId = store.ProviderStoreId,
                    pricingType = "med",
                    strainTypes = Array.Empty<string>(),
                    subcategories = Array.Empty<string>(),
                    Status = "Active",
                    types = Array.Empty<string>(),
                    useCache = false,
                    isDefaultSort = true,
                    sortDirection = 1,
                    bypassOnlineThresholds = false,
                    ignoreQuantityThresholds = false,
                    isKioskMenu = false,
                    removeProductsBelowOptionThresholds = true,
                    platformType = "ONLINE_MENU",
                    preOrderType = (string?)null,
                },
                page,
                perPage = 100,
            };
            var res = await QueryAsync(config.GraphqlUrl, "FilteredProducts", variables, config.FilteredProductsHash, ct);
            if (res["errors"] is JsonArray { Count: > 0 } errors)
                throw new ProviderException($"Dutchie: {errors[0]?["message"]} (the persisted query hash may need updating in providers.json)");

            var fp = res["data"]?["filteredProducts"];
            foreach (var p in fp.Arr("products"))
                items.AddRange(Map(store, p));
            if (page + 1 >= (fp?["queryInfo"].Int("totalPages") ?? 1)) break;
        }
        return items;
    }

    internal IEnumerable<InventoryItem> Map(StoreInfo store, JsonNode p)
    {
        var id = p.Str("id") ?? p.Str("_id");
        var name = p.Str("Name");
        if (id is null || name is null) yield break;

        var options = p.Arr("Options").Select(o => o.GetValue<string>()).ToList();
        if (options.Count == 0) options.Add("each");
        var medPrices = p.Arr("medicalPrices").Select(x => Json.Dec(x)).ToList();
        var prices = p.Arr("Prices").Select(x => Json.Dec(x)).ToList();
        var specials = p.Arr("medicalSpecialPrices").Select(x => Json.Dec(x)).ToList();
        var children = p["POSMetaData"].Arr("children").ToList();
        var type = p.Str("type");
        var subcategory = p.Str("subcategory");
        var strain = p.Str("strainType") is { } st && st != "N/A" ? st : null;
        var active = p.Str("Status") == "Active" && !p.Bool("isBelowThreshold");
        var belowThreshold = p.Arr("optionsBelowThreshold").Select(o => o.ToString()).ToHashSet();

        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var price = At(medPrices, i) ?? At(prices, i);
            var special = At(specials, i);
            var child = children.FirstOrDefault(c => c.Str("option") == option);
            var qty = child.Int("quantityAvailable");
            var package = child.Str("canonicalPackageId");

            yield return new InventoryItem
            {
                StoreKey = store.Key,
                ProductId = id,
                VariantId = option,
                Name = name,
                Brand = p.Str("brandName"),
                RawCategory = subcategory is null ? type : $"{type} / {subcategory}",
                Category = CategoryNormalizer.Normalize(type, name, subcategory),
                Strain = strain,
                Size = option,
                Price = price,
                SalePrice = special is { } s && s < price ? s : null,
                Quantity = qty,
                InStock = active && !belowThreshold.Contains(option) && (qty ?? 1) > 0,
                Potency = FormatPotency(p["THCContent"], "THC") ?? FormatPotency(p["CBDContent"], "CBD"),
                Batches = package is null || (qty ?? 0) <= 0 ? null : new() { [package] = $"package {package}" },
                Url = p.Str("cName") is { } slug ? $"https://dutchie.com/dispensary/{store.Slug}/product/{slug}" : store.MenuUrl,
                ImageUrl = p.Str("Image"),
                ThumbnailUrl = ThumbnailFor(p.Str("Image")),
            };
        }
    }

    private const string ImageBucket = "https://s3-us-west-2.amazonaws.com/dutchie-images/";

    /// <summary>Dutchie's image CDN resizes bucket images by id.</summary>
    internal static string? ThumbnailFor(string? image) =>
        image is not null && image.StartsWith(ImageBucket, StringComparison.OrdinalIgnoreCase) && !image[ImageBucket.Length..].Contains('/')
            ? $"https://images.dutchie.com/{image[ImageBucket.Length..]}?w=240&fm=jpg"
            : null;

    private static decimal? At(List<decimal?> list, int i) => i < list.Count ? list[i] : list.LastOrDefault();

    private static string? FormatPotency(JsonNode? content, string label)
    {
        var range = content.Arr("range").Select(x => Json.Dec(x)).OfType<decimal>().Where(v => v > 0).ToList();
        if (range.Count == 0) return null;
        var unit = content.Str("unit") == "PERCENTAGE" ? "%" : " mg";
        var lo = range.Min().ToString("0.#", CultureInfo.InvariantCulture);
        var hi = range.Max().ToString("0.#", CultureInfo.InvariantCulture);
        return lo == hi ? $"{label} {lo}{unit}" : $"{label} {lo}–{hi}{unit}";
    }

    private Task<JsonNode> QueryAsync(string endpoint, string operation, object variables, string hash, CancellationToken ct)
    {
        var vars = Uri.EscapeDataString(JsonSerializer.Serialize(variables));
        var ext = Uri.EscapeDataString(JsonSerializer.Serialize(new { persistedQuery = new { version = 1, sha256Hash = hash } }));
        // Apollo's CSRF guard rejects simple GETs unless a preflight-forcing header is present.
        return http.GetJsonAsync($"{endpoint}?operationName={operation}&variables={vars}&extensions={ext}", ct,
            new Dictionary<string, string>
            {
                ["apollographql-client-name"] = "Marketplace (production)",
                ["apollo-require-preflight"] = "true",
                ["Content-Type"] = "application/json",
            });
    }
}

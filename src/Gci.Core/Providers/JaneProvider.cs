using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Providers;

/// <summary>
/// Jane (iheartjane) powers Fine Fettle's menu. Products come from Jane's public Algolia index, which lists only
/// in-stock items. Jane has no raw warehouse count, but its per-weight <c>max_cart_quantity_*</c> field is the
/// per-order cart limit clamped to the remaining stock — so it equals the true count once stock falls below the
/// store's purchase cap, and only saturates (at <see cref="JaneConfig.MaxCartCap"/>) when a variant is well stocked.
/// We surface that as the quantity, flagging saturated values as a lower bound ("N+").
/// </summary>
public sealed partial class JaneProvider(ProviderHttp http, JaneConfig config) : IInventoryProvider
{
    private static readonly (string Key, string Label)[] Weights =
    {
        ("half_gram", "0.5g"), ("gram", "1g"), ("two_gram", "2g"), ("eighth_ounce", "3.5g"),
        ("quarter_ounce", "7g"), ("half_ounce", "14g"), ("ounce", "28g"), ("each", "each"),
    };

    public ProviderKind Kind => ProviderKind.Jane;

    public async Task<IReadOnlyList<StoreInfo>> DiscoverStoresAsync(CancellationToken ct)
    {
        if (!config.Enabled) return [];
        var stores = new List<StoreInfo>();
        foreach (var seed in config.Stores)
        {
            JsonNode? s = null;
            try
            {
                s = (await http.GetJsonAsync($"{config.StoreApi}/{seed.Id}", ct))["store"];
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Use the seed details alone.
            }

            var slug = s.Str("url_slug");
            stores.Add(new StoreInfo
            {
                Key = $"jane:{seed.Id}",
                Provider = ProviderKind.Jane,
                ProviderStoreId = seed.Id,
                Operator = seed.Operator,
                Name = seed.Name ?? StripOperator(s.Str("name"), seed.Operator) ?? seed.Id,
                City = s.Str("city") ?? seed.City,
                Address = s.Str("address") ?? seed.Address,
                Phone = s.Str("phone") ?? seed.Phone,
                Slug = slug,
                MenuUrl = seed.ShopUrl ?? (slug is null ? null : $"https://www.iheartjane.com/stores/{seed.Id}/{slug}/menu"),
            });
        }
        return stores;
    }

    public async Task<IReadOnlyList<InventoryItem>> FetchInventoryAsync(StoreInfo store, CancellationToken ct)
    {
        var url = $"https://search.iheartjane.com/1/indexes/{config.AlgoliaIndex}/query" +
                  $"?x-algolia-application-id={config.AlgoliaAppId}&x-algolia-api-key={config.AlgoliaApiKey}";
        var items = new List<InventoryItem>();
        for (var page = 0; page < 10; page++)
        {
            var body = new { query = "", filters = $"store_id : {store.ProviderStoreId}", hitsPerPage = 1000, page };
            var res = await http.PostJsonAsync(url, body, ct);
            foreach (var hit in res.Arr("hits"))
                items.AddRange(Map(store, hit));
            if (page + 1 >= (res.Int("nbPages") ?? 1)) break;
        }
        return items;
    }

    internal IEnumerable<InventoryItem> Map(StoreInfo store, JsonNode hit)
    {
        var productId = hit.Str("product_id");
        var name = hit.Str("name");
        if (productId is null || name is null) yield break;
        name = Spaces().Replace(name, " ").Trim();

        var kind = hit.Str("kind");
        var subtype = hit.Str("kind_subtype") ?? hit.Str("root_subtype");
        var rawCategory = subtype is null ? kind : $"{kind} / {subtype}";
        var category = CategoryNormalizer.Normalize(kind, name, subtype);
        var strain = hit.Str("category") is { } c ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c) : null;
        var potencies = hit.Arr("inventory_potencies").ToDictionary(x => x.Str("price_id") ?? "", x => x);
        var available = hit.Arr("available_weights").Select(w => w.GetValue<string>().Replace(' ', '_')).ToHashSet();
        var photo = hit.Arr("product_photos").FirstOrDefault()?["urls"];
        var image = photo.Str("original") ?? hit.Arr("image_urls").FirstOrDefault()?.GetValue<string>();
        var thumbnail = photo.Str("medium") ?? photo.Str("small");
        var url = store.Slug is null
            ? store.MenuUrl
            : $"https://www.iheartjane.com/stores/{store.ProviderStoreId}/{store.Slug}/products/{productId}/{hit.Str("url_slug")}";

        foreach (var (key, label) in Weights)
        {
            var price = hit.Dec($"price_{key}");
            if (price is null || (available.Count > 0 && !available.Contains(key))) continue;

            var special = hit.Dec($"special_price_{key}") ?? hit.Dec($"discounted_price_{key}");
            var thc = Json.Dec(potencies.GetValueOrDefault(key)?["thc_potency"]) ?? hit.Dec("percent_thc");

            // Jane's per-weight cart limit tracks the remaining count while stock is low; clamp the ceiling so a
            // well-stocked variant reads a stable "cap+" instead of a fluctuating raw limit.
            var cap = hit.Int($"max_cart_quantity_{key}") ?? hit.Int("max_cart_quantity");
            var qty = cap is { } limit && limit > 0 ? Math.Min(limit, config.MaxCartCap) : (int?)null;
            var atLeast = cap is { } raw && raw >= config.MaxCartCap;

            yield return new InventoryItem
            {
                StoreKey = store.Key,
                ProductId = productId,
                VariantId = key,
                Name = name,
                Brand = hit.Str("brand"),
                RawCategory = rawCategory,
                Category = category,
                Strain = strain,
                Size = key == "each" ? hit.Str("amount") ?? "each" : label,
                Price = price,
                SalePrice = special is { } s && s < price ? s : null,
                Quantity = qty,
                QuantityAtLeast = atLeast,
                InStock = true,
                Potency = Json.FormatPercent(thc, "THC"),
                Url = url,
                ImageUrl = image,
                ThumbnailUrl = thumbnail,
            };
        }
    }

    private static string? StripOperator(string? name, string op) =>
        name is null ? null : name.Replace(op, "", StringComparison.OrdinalIgnoreCase).Trim(' ', '-', '–');

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

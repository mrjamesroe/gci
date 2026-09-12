using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Providers;

/// <summary>
/// Trulieve's Adobe Commerce GraphQL. The menu is public per store view (selected with the
/// "Store" header); the medical ID on file is only enforced at checkout.
/// Variants are production batches, so they're rolled up into one item per product.
/// </summary>
public sealed partial class TrulieveProvider(ProviderHttp http, TrulieveConfig config) : IInventoryProvider
{
    public ProviderKind Kind => ProviderKind.Trulieve;

    private const string ProductsQuery = """
        query products($pageSize: Int, $currentPage: Int) {
          products(search: "", pageSize: $pageSize, currentPage: $currentPage) {
            total_count
            page_info { current_page total_pages }
            items {
              __typename id name sku available_quantity stock_status url_key
              small_image { url }
              custom_attributes_product { code value }
              categories { url_path }
              price_range { minimum_price { regular_price { value } final_price { value } } }
              ... on ConfigurableProduct {
                variants {
                  attributes { code label }
                  product { sku available_quantity stock_status
                    price_range { minimum_price { regular_price { value } final_price { value } } } }
                }
              }
            }
          }
        }
        """;

    public async Task<IReadOnlyList<StoreInfo>> DiscoverStoresAsync(CancellationToken ct)
    {
        if (!config.Enabled) return [];
        try
        {
            var html = await http.GetStringAsync($"{config.BaseUrl}/dispensaries/{config.StateSlug}", ct);
            var match = NextDataRegex().Match(html);
            if (match.Success)
            {
                var data = JsonNode.Parse(match.Groups[1].Value);
                var stores = data?["props"]?["pageProps"].Arr("stateStores")
                    .Where(s => s.Bool("is_active") && !s.Bool("non_shop_store") && !s.Bool("coming_soon"))
                    .Select(s => Build(s.Str("code")!, s.Str("name"), s.Str("city"), s.Str("address"), s.Str("phone")))
                    .ToList();
                if (stores is { Count: > 0 }) return stores;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Fall through to the configured list.
        }

        return config.FallbackStores.Select(s => Build(s.Id, s.Name, s.City, s.Address, s.Phone)).ToList();
    }

    private StoreInfo Build(string code, string? name, string? city, string? address, string? phone) => new()
    {
        Key = $"trulieve:{code}",
        Provider = ProviderKind.Trulieve,
        ProviderStoreId = code,
        Operator = "Trulieve",
        Name = name ?? city ?? code,
        City = city,
        Address = address,
        Phone = phone,
        MenuUrl = $"{config.BaseUrl}/dispensaries/{config.StateSlug}/{code.Replace("_ga", "")}",
    };

    public async Task<IReadOnlyList<InventoryItem>> FetchInventoryAsync(StoreInfo store, CancellationToken ct)
    {
        var items = new List<InventoryItem>();
        var headers = new Dictionary<string, string> { ["Store"] = store.ProviderStoreId };
        for (var page = 1; page <= 50; page++)
        {
            var body = new { query = ProductsQuery, variables = new { pageSize = config.PageSize, currentPage = page } };
            var res = await http.PostJsonAsync($"{config.BaseUrl}/api/graphql", body, ct, headers);
            if (res["errors"] is JsonArray { Count: > 0 } errors)
                throw new ProviderException($"Trulieve GraphQL: {errors[0]?["message"]}");

            var products = res["data"]?["products"];
            foreach (var p in products.Arr("items"))
                if (Map(store, p) is { } item) items.Add(item);

            var totalPages = products?["page_info"].Int("total_pages") ?? 1;
            if (page >= totalPages) break;
        }
        return items;
    }

    internal InventoryItem? Map(StoreInfo store, JsonNode p)
    {
        var name = p.Str("name");
        var sku = p.Str("sku");
        if (name is null || sku is null) return null;

        var attrs = p.Arr("custom_attributes_product")
            .Where(a => a.Str("code") is not null)
            .GroupBy(a => a.Str("code")!)
            .ToDictionary(g => g.Key, g => g.First().Str("value"));
        var paths = p.Arr("categories").Select(c => c.Str("url_path")).OfType<string>().ToList();
        var primary = paths.FirstOrDefault(IsProductCategory);
        var raw = paths.Where(IsProductCategory).Select(x => x.Split('/').Last()).Distinct().ToList();

        var variants = p.Arr("variants").ToList();
        int? qty;
        bool inStock;
        decimal? regular, final;
        string? potency;
        Dictionary<string, string>? batches = null;
        if (variants.Count > 0)
        {
            batches = new();
            foreach (var v in variants.Where(v => v["product"].Str("stock_status") == "IN_STOCK"
                                                  && (v["product"].Int("available_quantity") ?? 0) > 0))
            {
                var vattrs = v.Arr("attributes").ToList();
                var code = vattrs.FirstOrDefault(a => a.Str("code") == "batch_codes").Str("label") ?? v["product"].Str("sku");
                var thcLabel = vattrs.FirstOrDefault(a => a.Str("code") == "thc_percentage").Str("label");
                if (code is not null)
                    batches[code] = thcLabel is null ? $"batch {code}" : $"THC {thcLabel}%";
            }

            var live = variants.Where(v => v["product"].Str("stock_status") == "IN_STOCK").ToList();
            qty = variants.Sum(v => Math.Max(0, v["product"].Int("available_quantity") ?? 0));
            inStock = live.Count > 0 && qty > 0;
            var priced = (live.Count > 0 ? live : variants).Select(v => v["product"]?["price_range"]?["minimum_price"]).ToList();
            final = priced.Select(m => Json.Dec(m?["final_price"]?["value"])).Min();
            regular = priced.Select(m => Json.Dec(m?["regular_price"]?["value"])).Min();
            var thc = (live.Count > 0 ? live : variants)
                .SelectMany(v => v.Arr("attributes"))
                .Where(a => a.Str("code") == "thc_percentage")
                .Select(a => decimal.TryParse(a.Str("label"), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
                .OfType<decimal>().Distinct().Order().ToList();
            potency = thc.Count switch
            {
                0 => null,
                1 => $"THC {thc[0]:0.#}%",
                _ => $"THC {thc[0]:0.#}–{thc[^1]:0.#}%",
            };
        }
        else
        {
            qty = p.Int("available_quantity");
            inStock = p.Str("stock_status") == "IN_STOCK" && (qty ?? 1) > 0;
            final = Json.Dec(p["price_range"]?["minimum_price"]?["final_price"]?["value"]);
            regular = Json.Dec(p["price_range"]?["minimum_price"]?["regular_price"]?["value"]);
            potency = null;
        }

        var rawCategory = string.Join(", ", raw);
        return new InventoryItem
        {
            StoreKey = store.Key,
            ProductId = sku,
            VariantId = "default",
            Name = name,
            Brand = attrs.GetValueOrDefault("brand"),
            RawCategory = rawCategory,
            Category = CategoryNormalizer.Normalize(primary is null ? rawCategory : $"{rawCategory} {primary}", name),
            Strain = attrs.GetValueOrDefault("strain_type"),
            Size = attrs.GetValueOrDefault("units"),
            Price = regular ?? final,
            SalePrice = final < regular ? final : null,
            Quantity = qty,
            InStock = inStock,
            Potency = potency,
            Batches = batches,
            // ?store=&state= makes trulieve.com select this store (it stores the choice and strips the params),
            // so a phone that has never visited opens the right store's page instead of a default one.
            Url = p.Str("url_key") is { } key
                ? $"{config.BaseUrl}/product/{key}?store={Uri.EscapeDataString(store.ProviderStoreId)}&state={config.StateSlug}"
                : store.MenuUrl,
            ImageUrl = p["small_image"].Str("url"),
        };
    }

    private static bool IsProductCategory(string? path) =>
        path is not null && path != "brands" && !path.StartsWith("brands/") && !path.Contains("deals")
        && !path.StartsWith("ga-") && !path.StartsWith("fl-");

    [GeneratedRegex("<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();
}

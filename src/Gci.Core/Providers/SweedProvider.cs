using System.Globalization;
using System.Text.Json.Nodes;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Providers;

/// <summary>Sweed powers Treevana Remedy's shop. The store is selected with a "storeid" header.</summary>
public sealed class SweedProvider(ProviderHttp http, SweedConfig config) : IInventoryProvider
{
    private const int PageSize = 100;

    public ProviderKind Kind => ProviderKind.Sweed;

    public async Task<IReadOnlyList<StoreInfo>> DiscoverStoresAsync(CancellationToken ct)
    {
        if (!config.Enabled) return [];
        var stores = new List<StoreInfo>();
        foreach (var seed in config.Stores)
        {
            JsonNode? info = null;
            try
            {
                info = (await http.PostJsonAsync($"{config.ApiBase}/Store/GetStoreInfoV2", new { }, ct, Headers(seed.Id)))["storeInfo"];
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Seed details are enough to fetch the menu.
            }

            stores.Add(new StoreInfo
            {
                Key = $"sweed:{seed.Id}",
                Provider = ProviderKind.Sweed,
                ProviderStoreId = seed.Id,
                Operator = seed.Operator,
                Name = seed.Name ?? info.Str("name") ?? seed.Id,
                City = seed.City,
                Address = info?["contacts"].Str("address") ?? seed.Address,
                Phone = FormatPhone(info?["contacts"].Str("phone")) ?? seed.Phone,
                Slug = seed.ShopUrl,
                MenuUrl = seed.ShopUrl is null ? null : $"{seed.ShopUrl.TrimEnd('/')}/menu",
            });
        }
        return stores;
    }

    public async Task<IReadOnlyList<InventoryItem>> FetchInventoryAsync(StoreInfo store, CancellationToken ct)
    {
        var items = new List<InventoryItem>();
        var seen = 0;
        for (var page = 1; page <= 20; page++)
        {
            var body = new { filters = new { }, page, pageSize = PageSize, sortingMethodId = 7, searchTerm = "", platformOs = "web", sourcePage = 1 };
            var res = await http.PostJsonAsync($"{config.ApiBase}/Products/GetProductList", body, ct, Headers(store.ProviderStoreId));
            var list = res.Arr("list").ToList();
            foreach (var p in list)
                items.AddRange(Map(store, p));
            seen += list.Count;
            if (list.Count == 0 || seen >= (res.Int("total") ?? 0)) break;
        }
        return items;
    }

    internal IEnumerable<InventoryItem> Map(StoreInfo store, JsonNode p)
    {
        var id = p.Str("id");
        var name = p.Str("name");
        if (id is null || name is null) yield break;

        var categoryName = p["category"].Str("name");
        var categorySlug = p["category"].Str("canonicalName");
        var subtype = p["productType"].Str("name") ?? p["subcategory"].Str("name");
        var category = CategoryNormalizer.Normalize(categoryName, name, subtype);
        var image = p.Arr("images").FirstOrDefault()?.GetValue<string>();
        var url = store.Slug is { } shop && categorySlug is not null && p.Str("canonicalName") is { } slug
            ? $"{shop.TrimEnd('/')}/menu/{categorySlug}/{slug}-{id}"
            : store.MenuUrl;

        foreach (var v in p.Arr("variants"))
        {
            var qty = v.Dec("availableQty");
            var available = v["orderingAvailability"].Str("reason") is null or "Available";
            var price = v.Dec("price");
            var promo = v.Dec("promoPrice");
            yield return new InventoryItem
            {
                StoreKey = store.Key,
                ProductId = id,
                VariantId = v.Str("id") ?? "default",
                Name = name,
                Brand = p["brand"].Str("name"),
                RawCategory = subtype is null ? categoryName : $"{categoryName} / {subtype}",
                Category = category,
                Strain = p["strain"]?["prevalence"].Str("name"),
                Size = v.Str("name"),
                Price = price,
                SalePrice = promo is { } pr && pr < price ? pr : null,
                Quantity = qty is null ? null : (int)Math.Floor(qty.Value),
                InStock = available && (qty ?? 1) > 0,
                Potency = FormatLab(v["labTests"]?["thc"], "THC") ?? FormatLab(v["labTests"]?["cbd"], "CBD"),
                Url = url,
                ImageUrl = image,
                // Sweed's media host snaps ?width= to its nearest preset (240 → 320px).
                ThumbnailUrl = image is null || image.Contains('?') ? null : $"{image}?width=240",
            };
        }
    }

    private static string? FormatLab(JsonNode? lab, string label)
    {
        var values = lab.Arr("value").Select(x => Json.Dec(x)).OfType<decimal>().ToList();
        if (values.Count == 0) return null;
        var unit = lab.Str("unitAbbr") ?? "";
        var sep = unit == "%" ? "" : " ";
        return $"{label} {values.Max().ToString("0.#", CultureInfo.InvariantCulture)}{sep}{unit}".TrimEnd();
    }

    private static Dictionary<string, string> Headers(string storeId) => new() { ["storeid"] = storeId, ["ssr"] = "false" };

    private static string? FormatPhone(string? digits)
    {
        if (digits is null) return null;
        var d = new string(digits.Where(char.IsDigit).ToArray());
        if (d.Length == 11 && d[0] == '1') d = d[1..];
        return d.Length == 10 ? $"({d[..3]}) {d[3..6]}-{d[6..]}" : digits;
    }
}

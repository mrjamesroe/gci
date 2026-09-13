using System.Text.Json.Nodes;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Providers;

/// <summary>
/// Mosaic (mosaic.green) powers shop.botanicalsciences.com: Botanical Sciences' own dispensaries
/// plus the independent pharmacies that carry their products (e.g. Lotus Farmacy).
/// The product list is fixed at 12 per page, so fetching pages through it.
/// </summary>
public sealed class MosaicProvider(ProviderHttp http, MosaicConfig config, Action<StoreInfo, JsonNode>? onBatchSample = null) : IInventoryProvider
{
    private const int MaxPages = 40;

    /// <summary>True once a product carries a non-empty <c>inventory_batches</c> (Georgia stores usually leave it empty).</summary>
    internal static bool HasInventoryBatches(JsonNode product) =>
        product.Arr("product_variants").Any(v => v.Arr("inventory_batches").Any());

    public ProviderKind Kind => ProviderKind.Mosaic;

    public async Task<IReadOnlyList<StoreInfo>> DiscoverStoresAsync(CancellationToken ct)
    {
        if (!config.Enabled || string.IsNullOrEmpty(config.CompanyId)) return [];
        var res = await http.GetJsonAsync(
            $"{config.ApiBase}/merchant/company/{config.CompanyId}/stores?page_size=1000&is_visible=true", ct);

        return res.Arr("store_list")
            .Where(s => s.Bool("is_visible") && s.Str("store_status") == "ACTIVE")
            .Select(s =>
            {
                var name = s.Str("name") ?? "Unknown";
                var slug = s.Str("store_slug");
                var addr = s["address"];
                return new StoreInfo
                {
                    Key = $"mosaic:{s.Str("id")}",
                    Provider = ProviderKind.Mosaic,
                    ProviderStoreId = s.Str("id")!,
                    Operator = config.Operator,
                    Name = name,
                    City = addr.Str("city"),
                    Address = addr.Str("street1"),
                    Phone = FormatPhone(s.Str("telephone")),
                    Slug = slug,
                    MenuUrl = slug is null ? config.ShopBase : $"{config.ShopBase}/products?store={slug}",
                    // Operator-run stores are named by city; partner pharmacies are "Pharmacy - City".
                    IsPharmacyPartner = name.Contains(" - ") || name.Contains(" – "),
                };
            })
            .OrderBy(s => s.IsPharmacyPartner).ThenBy(s => s.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<InventoryItem>> FetchInventoryAsync(StoreInfo store, CancellationToken ct)
    {
        var items = new List<InventoryItem>();
        var url = $"{config.ApiBase}/cms/{config.CompanyId}/{store.ProviderStoreId}/product-list";
        var seenProducts = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var res = await http.PostJsonAsync(url, new { current_page = page }, ct);
            var products = res.Arr("products").ToList();
            foreach (var p in products)
                items.AddRange(Map(store, p));

            if (onBatchSample is not null && products.FirstOrDefault(HasInventoryBatches) is { } withBatch)
                onBatchSample(store, withBatch);

            seenProducts += products.Count;
            var total = res.Int("total_records") ?? 0;
            if (products.Count == 0 || seenProducts >= total) break;
        }
        return items;
    }

    internal IEnumerable<InventoryItem> Map(StoreInfo store, JsonNode p)
    {
        var productId = p.Str("product_id");
        var name = p.Str("name");
        if (productId is null || name is null) yield break;

        var attrs = p.Arr("attributes").ToList();
        string? Attr(string type) => attrs.FirstOrDefault(a => a.Str("type") == type) is { } a ? a.Str("label") : null;
        string? AttrValue(string type) => attrs.FirstOrDefault(a => a.Str("type") == type) is { } a ? a.Str("value") : null;

        var brand = Attr("BRAND") ?? p.Str("brand_name");
        var rawCategory = p.Str("category_id") ?? p.Str("category_slug");
        var potency = string.Join(" · ", new[] { AttrValue("THC") is { } thc ? $"THC {thc}" : null,
                                                   AttrValue("CBD") is { } cbd ? $"CBD {cbd}" : null }.OfType<string>());
        var slug = p.Str("product_slug");
        var media = p.Arr("media").FirstOrDefault();
        var image = media.Str("src");
        var thumbnail = media?["optimized_media"].Arr("image_thumbnails")
            .FirstOrDefault(t => t.Str("size") == "480" && t.Str("mime_type") == "image/jpeg").Str("url");

        foreach (var v in p.Arr("product_variants"))
        {
            var stock = v.Int("stock");
            yield return new InventoryItem
            {
                StoreKey = store.Key,
                ProductId = productId,
                VariantId = v.Str("product_variant_id") ?? "default",
                Name = name,
                Brand = brand,
                RawCategory = rawCategory,
                Category = CategoryNormalizer.Normalize(p.Str("category_slug") ?? rawCategory, name, Attr("SUB_TYPE")),
                Strain = Attr("FLOWER"),
                Size = v.Str("label"),
                Price = v.Dec("price"),
                SalePrice = v.Dec("discounted_price") is { } d && d < (v.Dec("price") ?? decimal.MaxValue) ? d : null,
                Quantity = stock,
                InStock = v.Bool("has_stock") && (stock ?? 1) > 0 && (v["sellable"] is null || v.Bool("sellable")),
                Potency = potency.Length > 0 ? potency : null,
                Url = slug is null ? store.MenuUrl : $"{config.ShopBase}/products/{slug}?store={store.Slug}",
                ImageUrl = image,
                ThumbnailUrl = thumbnail,
            };
        }
    }

    private static string? FormatPhone(string? digits)
    {
        if (digits is null || digits.Length != 10 || !digits.All(char.IsDigit)) return digits;
        return $"({digits[..3]}) {digits[3..6]}-{digits[6..]}";
    }
}

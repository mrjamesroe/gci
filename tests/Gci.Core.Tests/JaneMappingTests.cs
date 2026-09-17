using System.Text.Json.Nodes;
using Gci.Core.Models;
using Gci.Core.Providers;

namespace Gci.Core.Tests;

public class JaneMappingTests
{
    private static readonly StoreInfo Store = new()
    {
        Key = "jane:6118", Provider = ProviderKind.Jane, ProviderStoreId = "6118",
        Operator = "Fine Fettle", Name = "Smyrna", Slug = "fine-fettle-smyrna",
    };

    // Trimmed from a live Fine Fettle Smyrna response (2026-09-16). Jane exposes no warehouse count; the per-weight
    // max_cart_quantity is a purchase cap clamped to remaining stock, so a low value is the exact count.
    private const string LowStock = """
        {
          "product_id": "111", "name": "Live Rosin Badder", "kind": "extract", "kind_subtype": "rosin",
          "brand": "Golden", "available_weights": ["half gram"],
          "price_half_gram": 55, "max_cart_quantity_half_gram": 3, "percent_thc": 74.2
        }
        """;

    private const string WellStocked = """
        {
          "product_id": "222", "name": "House Cartridge", "kind": "vape", "available_weights": ["gram"],
          "price_gram": 40, "max_cart_quantity_gram": 45, "percent_thc": 88.0
        }
        """;

    private static InventoryItem MapOne(string json)
    {
        using var http = new ProviderHttp("test", new HttpClientHandler());
        var provider = new JaneProvider(http, new JaneConfig());
        return provider.Map(Store, JsonNode.Parse(json)!).Single();
    }

    [Fact]
    public void Low_cart_limit_is_the_exact_remaining_count()
    {
        var item = MapOne(LowStock);

        Assert.Equal(3, item.Quantity);
        Assert.False(item.QuantityAtLeast);
        Assert.True(item.InStock);
    }

    [Fact]
    public void Cart_limit_at_or_above_the_cap_is_clamped_and_flagged_as_a_lower_bound()
    {
        var item = MapOne(WellStocked); // raw limit 45, cap 30

        Assert.Equal(30, item.Quantity);
        Assert.True(item.QuantityAtLeast);
    }
}

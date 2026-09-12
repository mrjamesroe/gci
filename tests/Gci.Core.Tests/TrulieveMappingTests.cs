using System.Text.Json.Nodes;
using Gci.Core.Models;
using Gci.Core.Providers;

namespace Gci.Core.Tests;

public class TrulieveMappingTests
{
    private static readonly StoreInfo Store = new()
    {
        Key = "trulieve:marietta", Provider = ProviderKind.Trulieve, ProviderStoreId = "marietta",
        Operator = "Trulieve", Name = "Marietta",
    };

    // Trimmed from a live Marietta response (2026-09-11).
    private const string RsoSyringe = """
        {
          "__typename": "ConfigurableProduct", "id": 1, "name": "RSO Syringe", "sku": "126710",
          "available_quantity": 529, "stock_status": "IN_STOCK", "url_key": "momenta-rso-syringe-126710",
          "custom_attributes_product": [ { "code": "brand", "value": "Momenta" }, { "code": "units", "value": "1g" },
                                         { "code": "strain_type", "value": "Indica" } ],
          "categories": [ { "url_path": "brands" }, { "url_path": "oral" }, { "url_path": "oral/rso-syringes" },
                          { "url_path": "brands/momenta" }, { "url_path": "ga-bundle-deals" } ],
          "variants": [
            { "attributes": [ { "code": "thc_percentage", "label": "78" }, { "code": "batch_codes", "label": "0008919895" } ],
              "product": { "sku": "126710_0008919895", "available_quantity": 215, "stock_status": "IN_STOCK",
                           "price_range": { "minimum_price": { "regular_price": { "value": 55 }, "final_price": { "value": 55 } } } } },
            { "attributes": [ { "code": "thc_percentage", "label": "84" }, { "code": "batch_codes", "label": "0008957623" } ],
              "product": { "sku": "126710_0008957623", "available_quantity": 314, "stock_status": "IN_STOCK",
                           "price_range": { "minimum_price": { "regular_price": { "value": 55 }, "final_price": { "value": 44 } } } } },
            { "attributes": [ { "code": "thc_percentage", "label": "68" }, { "code": "batch_codes", "label": "0008697643" } ],
              "product": { "sku": "126710_0008697643", "available_quantity": 0, "stock_status": "OUT_OF_STOCK",
                           "price_range": { "minimum_price": { "regular_price": { "value": 55 }, "final_price": { "value": 55 } } } } }
          ]
        }
        """;

    [Fact]
    public void Batches_roll_up_into_one_item_with_total_quantity_and_live_batches()
    {
        using var http = new ProviderHttp("test", new HttpClientHandler());
        var provider = new TrulieveProvider(http, new TrulieveConfig());

        var item = provider.Map(Store, JsonNode.Parse(RsoSyringe)!)!;

        Assert.Equal("126710", item.ProductId);
        Assert.Equal(529, item.Quantity);
        Assert.True(item.InStock);
        Assert.Equal(ProductCategory.Oral, item.Category);
        Assert.Equal("Momenta", item.Brand);
        Assert.Equal("1g", item.Size);
        Assert.Equal(55, item.Price);
        Assert.Equal(44, item.SalePrice);
        Assert.Equal("THC 78–84%", item.Potency);
        Assert.Equal(new[] { "0008919895", "0008957623" }, item.Batches!.Keys.Order());
        Assert.Equal("https://www.trulieve.com/product/momenta-rso-syringe-126710", item.Url);
    }
}

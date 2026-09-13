using System.Text.Json.Nodes;
using Gci.Core.Models;
using Gci.Core.Providers;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public class BatchSampleTests
{
    private static readonly StoreInfo Lotus = new()
    {
        Key = "mosaic:lotus", Provider = ProviderKind.Mosaic, ProviderStoreId = "lotus",
        Operator = "Botanical Sciences", Name = "Lotus Farmacy - Suwanee",
    };

    private static JsonNode Product(string name, string batches) => JsonNode.Parse(
        $$"""{"name":"{{name}}","product_variants":[{"label":"1g","inventory_batches":{{batches}}}]}""")!;

    [Fact]
    public void HasInventoryBatches_is_true_only_for_a_populated_array()
    {
        Assert.True(MosaicProvider.HasInventoryBatches(Product("Widget", """[{"batch_number":"B1"}]""")));
        Assert.False(MosaicProvider.HasInventoryBatches(Product("Widget", "[]")));
        Assert.False(MosaicProvider.HasInventoryBatches(JsonNode.Parse("""{"name":"X"}""")!));
    }

    [Fact]
    public void Capture_writes_the_first_populated_product_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gci-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new DataStore(dir);
            var writer = new BatchSampleWriter(data);

            writer.Capture(Lotus, Product("Abby's Tincture", """[{"batch_number":"B1","thc":"88"}]"""));
            writer.Capture(Lotus, Product("Later Product", """[{"batch_number":"B2"}]"""));   // ignored: only the first

            var path = Path.Combine(dir, BatchSampleWriter.FileName);
            Assert.True(File.Exists(path));
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal("Abby's Tincture", (string?)saved["productName"]);
            Assert.Equal("mosaic:lotus", (string?)saved["storeKey"]);
            Assert.Equal("B1", (string?)saved["product"]!["product_variants"]![0]!["inventory_batches"]![0]!["batch_number"]);
            Assert.NotNull(saved["capturedAt"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Capture_leaves_an_existing_sample_untouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gci-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new DataStore(dir);
            var path = Path.Combine(dir, BatchSampleWriter.FileName);
            File.WriteAllText(path, """{"productName":"already here"}""");

            new BatchSampleWriter(data).Capture(Lotus, Product("New One", """[{"batch_number":"B9"}]"""));

            Assert.Equal("already here", (string?)JsonNode.Parse(File.ReadAllText(path))!["productName"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

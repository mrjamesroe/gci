using System.IO;
using System.Text.Json.Nodes;
using Gci.Core.Models;
using Gci.Core.Providers;

namespace Gci.Core.Services;

/// <summary>
/// Records, once, the shape of the first product GCI observes carrying batch detail (Mosaic's <c>inventory_batches</c>),
/// so batch-level restock detection can be wired to the real structure. It only writes what a normal refresh already
/// fetched; it never makes a request of its own. After the file exists it does nothing.
/// </summary>
public sealed class BatchSampleWriter(DataStore data)
{
    public const string FileName = "mosaic-batch-sample.json";

    private readonly object _lock = new();
    private bool _done;

    public void Capture(StoreInfo store, JsonNode product)
    {
        lock (_lock)
        {
            if (_done) return;
            if (File.Exists(data.PathFor(FileName))) { _done = true; return; }

            var sample = new JsonObject
            {
                ["capturedAt"] = DateTimeOffset.Now.ToString("o"),
                ["note"] = "First product seen with a non-empty inventory_batches, saved so GCI can wire batch-level "
                           + "restock detection to the real shape. Safe to delete.",
                ["storeKey"] = store.Key,
                ["storeName"] = store.DisplayName,
                ["productName"] = product.Str("name"),
                ["product"] = product.DeepClone(),
            };
            data.Save(FileName, sample);
            _done = true;
        }
    }
}

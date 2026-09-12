using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gci.Core.Models;

namespace Gci.Core.Providers;

public interface IInventoryProvider
{
    ProviderKind Kind { get; }
    Task<IReadOnlyList<StoreInfo>> DiscoverStoresAsync(CancellationToken ct);
    Task<IReadOnlyList<InventoryItem>> FetchInventoryAsync(StoreInfo store, CancellationToken ct);
}

/// <summary>Forgiving accessors for loosely-typed provider JSON.</summary>
internal static class Json
{
    public static string? Str(this JsonNode? node, string prop)
    {
        var v = node?[prop];
        if (v is null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            return jv.ToJsonString();
        }
        return null;
    }

    public static decimal? Dec(this JsonNode? node, string prop) => Dec(node?[prop]);

    public static decimal? Dec(JsonNode? v)
    {
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<decimal>(out var d)) return d;
        if (jv.TryGetValue<double>(out var dbl)) return (decimal)dbl;
        if (jv.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static int? Int(this JsonNode? node, string prop)
    {
        var d = Dec(node?[prop]);
        return d is null ? null : (int)Math.Floor(d.Value);
    }

    public static bool Bool(this JsonNode? node, string prop) =>
        node?[prop] is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

    public static IEnumerable<JsonNode> Arr(this JsonNode? node, string prop) =>
        node?[prop] is JsonArray a ? a.OfType<JsonNode>() : Enumerable.Empty<JsonNode>();

    public static string Compact(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    public static string? FormatPercent(decimal? value, string label) =>
        value is { } v && v > 0 ? $"{label} {v.ToString("0.#", CultureInfo.InvariantCulture)}%" : null;
}

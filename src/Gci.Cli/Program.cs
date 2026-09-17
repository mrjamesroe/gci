using System.Globalization;
using Gci.Core.Models;
using Gci.Core.Services;

// gci-cli: headless access to the same engine the desktop app uses.
//   gci-cli stores                         list every store GCI can read
//   gci-cli menu <store-filter> [search]   print a store's in-stock menu
//   gci-cli find <search> [--category X]   search every store
//   gci-cli refresh [store-filter]         refresh and print detected changes
//   gci-cli audit                          emit the discovered store list as JSON (used by the weekly store-audit CI)
// Data lives in %LOCALAPPDATA%\GCI (shared with the app) unless --data <dir> is given.

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
var argv = args.ToList();
string? dataDir = TakeOption(argv, "--data");
string? categoryArg = TakeOption(argv, "--category");
var command = argv.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var rest = argv.Skip(1).ToList();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var data = new DataStore(dataDir);
using var service = new InventoryService(data);

switch (command)
{
    case "stores":
        await EnsureStoresAsync();
        foreach (var s in service.Stores.OrderBy(s => s.Operator).ThenBy(s => s.IsPharmacyPartner).ThenBy(s => s.Name))
            Console.WriteLine($"{s.Key,-50} {s.DisplayName,-50} {s.City,-16} {s.Phone}");
        Console.WriteLine($"{service.Stores.Count} stores");
        break;

    case "menu":
    {
        await EnsureStoresAsync();
        var stores = MatchStores(rest.FirstOrDefault() ?? "");
        await service.RefreshAsync(stores.Select(s => s.Key).ToList(), cts.Token);
        PrintItems(service.GetItems(stores.Select(s => s.Key)), rest.Skip(1).FirstOrDefault(), stores);
        PrintErrors(stores);
        break;
    }

    case "find":
    {
        await EnsureStoresAsync();
        var stores = service.Stores.Where(s => !s.IsPharmacyPartner || rest.Contains("--pharmacies")).ToList();
        await service.RefreshAsync(stores.Select(s => s.Key).ToList(), cts.Token,
            new Progress<(int Done, int Total)>(p => Console.Error.Write($"\rRefreshing {p.Done}/{p.Total} stores...")));
        Console.Error.WriteLine();
        PrintItems(service.GetItems(stores.Select(s => s.Key)), rest.FirstOrDefault(s => !s.StartsWith("--")), stores);
        PrintErrors(stores);
        break;
    }

    case "refresh":
    {
        await EnsureStoresAsync();
        var stores = rest.Count > 0 ? MatchStores(rest[0]) : service.Stores.Where(s => !s.IsPharmacyPartner).ToList();
        var result = await service.RefreshAsync(stores.Select(s => s.Key).ToList(), cts.Token);
        Console.WriteLine($"{result.StoresSucceeded} ok, {result.StoresFailed} failed");
        foreach (var e in result.Events.Where(e => e.Kind != ChangeKind.QuantityChanged))
            Console.WriteLine($"{e.Describe(),-24} {e.StoreName,-40} {e.Name} {e.Size} {e.Detail}");
        PrintErrors(stores);
        break;
    }

    case "audit":
    {
        // Fresh discovery, emitted as stable (key-sorted) JSON so the store-audit workflow can diff it against the
        // committed snapshot. Discovery warnings go to stderr and never abort the run — this is report-only.
        if (!rest.Contains("--rediscover")) rest.Add("--rediscover");
        await EnsureStoresAsync();
        var snapshot = service.Stores
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new
            {
                key = s.Key,
                provider = s.Provider.ToString(),
                @operator = s.Operator,
                name = s.Name,
                city = s.City,
                pharmacy = s.IsPharmacyPartner,
                menuUrl = s.MenuUrl,
            })
            .ToList();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        break;
    }

    default:
        Console.WriteLine("usage: gci-cli stores | menu <store> [search] | find <search> [--category Concentrate] [--pharmacies] | refresh [store] | audit [--data <dir>]");
        break;
}

async Task EnsureStoresAsync()
{
    if (service.Stores.Count > 0 && !rest.Contains("--rediscover")) return;
    foreach (var err in await service.DiscoverStoresAsync(cts.Token))
        Console.Error.WriteLine($"discovery warning: {err}");
}

List<StoreInfo> MatchStores(string filter)
{
    var matches = service.Stores.Where(s =>
        s.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0) throw new InvalidOperationException($"No store matches '{filter}'. Try: gci-cli stores");
    return matches;
}

void PrintItems(IReadOnlyList<InventoryItem> items, string? search, IReadOnlyList<StoreInfo> stores)
{
    var byKey = stores.ToDictionary(s => s.Key);
    ProductCategory? category = categoryArg is null ? null : Enum.Parse<ProductCategory>(categoryArg, ignoreCase: true);
    var rule = new WatchRule { Category = category, Keywords = search is null ? new() : new() { search } };
    var rows = items
        .Where(i => i.InStock && byKey.ContainsKey(i.StoreKey) && WatchMatcher.Matches(rule, i, byKey[i.StoreKey]))
        .OrderBy(i => i.Category).ThenBy(i => byKey[i.StoreKey].DisplayName).ThenBy(i => i.Name)
        .ToList();
    foreach (var i in rows)
        Console.WriteLine($"{i.Category,-11} {byKey[i.StoreKey].DisplayName,-34} {Trim(i.Name, 44),-44} {i.Size,-7} {i.EffectivePrice,8:C0} {(i.Quantity?.ToString() ?? "-"),5}  {i.Potency}");
    Console.WriteLine($"{rows.Count} items");
}

void PrintErrors(IEnumerable<StoreInfo> stores)
{
    foreach (var s in stores)
        if (service.GetStatus(s.Key)?.Error is { } err)
            Console.Error.WriteLine($"! {s.DisplayName}: {err}");
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public class InventoryDifferTests
{
    private static readonly StoreInfo Store = new()
    {
        Key = "trulieve:marietta", Provider = ProviderKind.Trulieve, ProviderStoreId = "marietta",
        Operator = "Trulieve", Name = "Marietta",
    };

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.FromHours(-4));

    private static InventoryItem Item(string id, int? qty = 10, decimal price = 50, bool inStock = true,
        Dictionary<string, string>? batches = null) => new()
    {
        StoreKey = Store.Key, ProductId = id, VariantId = "default", Name = $"Product {id}",
        Price = price, Quantity = qty, InStock = inStock, Batches = batches,
    };

    private static List<ChangeEvent> Diff(IReadOnlyCollection<InventoryItem>? before, IReadOnlyCollection<InventoryItem> after, ISet<string>? seen = null) =>
        InventoryDiffer.Diff(Store, before, after, seen ?? new HashSet<string>(), Now)
            .Where(e => e.Kind != ChangeKind.QuantityChanged).ToList();

    [Fact]
    public void First_refresh_is_a_silent_baseline()
    {
        var seen = new HashSet<string>();
        Assert.Empty(Diff(null, [Item("a"), Item("b")], seen));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Unseen_item_is_new_and_previously_seen_item_is_back_in_stock()
    {
        var seen = new HashSet<string>();
        Diff(null, [Item("a")], seen);
        Diff([Item("a")], [], seen); // sells out

        var events = Diff([], [Item("a"), Item("b")], seen);

        Assert.Contains(events, e => e.Kind == ChangeKind.BackInStock && e.Name == "Product a");
        Assert.Contains(events, e => e.Kind == ChangeKind.NewProduct && e.Name == "Product b");
    }

    [Fact]
    public void Missing_or_out_of_stock_item_is_sold_out()
    {
        var events = Diff([Item("a"), Item("b")], [Item("a", inStock: false)]);
        Assert.Equal(2, events.Count(e => e.Kind == ChangeKind.SoldOut));
    }

    [Fact]
    public void Price_changes_are_reported_with_direction()
    {
        var events = Diff([Item("a", price: 60), Item("b", price: 40)], [Item("a", price: 45), Item("b", price: 50)]);
        Assert.Contains(events, e => e.Kind == ChangeKind.PriceDrop && e.OldPrice == 60 && e.NewPrice == 45);
        Assert.Contains(events, e => e.Kind == ChangeKind.PriceIncrease && e.NewPrice == 50);
    }

    [Fact]
    public void New_batch_on_a_listed_product_is_a_restock_with_its_potency()
    {
        var before = Item("a", qty: 4, batches: new() { ["0001"] = "THC 78%" });
        var after = Item("a", qty: 6, batches: new() { ["0001"] = "THC 78%", ["0002"] = "THC 84%" });

        var restock = Assert.Single(Diff([before], [after]));

        Assert.Equal(ChangeKind.Restocked, restock.Kind);
        Assert.Equal("New batch: THC 84%", restock.Detail);
    }

    [Theory]
    [InlineData(4, 24, true)]    // big jump from a low count
    [InlineData(100, 130, true)] // +30 is ≥ 25%
    [InlineData(100, 110, false)] // +10 is < 25%
    [InlineData(2, 5, false)]    // +3 is under the 5-unit floor
    public void Quantity_jump_counts_as_restock_only_when_meaningful(int before, int after, bool expected)
    {
        var events = Diff([Item("a", qty: before)], [Item("a", qty: after)]);
        Assert.Equal(expected, events.Any(e => e.Kind == ChangeKind.Restocked));
    }

    [Fact]
    public void Quantity_changes_are_emitted_for_low_stock_rules()
    {
        var events = InventoryDiffer.Diff(Store, [Item("a", qty: 8)], [Item("a", qty: 3)], new HashSet<string>(), Now);
        var e = Assert.Single(events);
        Assert.Equal(ChangeKind.QuantityChanged, e.Kind);
        Assert.Equal((8, 3), (e.OldQuantity, e.NewQuantity));
    }
}

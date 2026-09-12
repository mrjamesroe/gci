using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public class WatchMatcherTests
{
    private static ChangeEvent Event(ChangeKind kind, string name = "Riddler Crumble 1g", ProductCategory category = ProductCategory.Concentrate,
        string store = "trulieve:marietta", decimal? price = 50, int? oldQty = null, int? newQty = null) => new()
    {
        At = DateTimeOffset.Now, Kind = kind, StoreKey = store, StoreName = "Trulieve Marietta", Operator = "Trulieve",
        ItemKey = $"{store}|x|default", Name = name, Brand = "Modern Flower", Category = category,
        NewPrice = price, OldQuantity = oldQty, NewQuantity = newQty,
    };

    [Fact]
    public void Keyword_entries_are_or_and_words_within_an_entry_are_and()
    {
        var rule = new WatchRule { Keywords = { "live rosin", "crumble" } };
        Assert.True(WatchMatcher.Matches(rule, Event(ChangeKind.NewProduct, "Riddler Crumble")));
        Assert.True(WatchMatcher.Matches(rule, Event(ChangeKind.NewProduct, "Blue Dream Live Rosin 1g")));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.NewProduct, "Blue Dream Live Resin Cart")));
    }

    [Fact]
    public void Category_store_and_price_filters_apply()
    {
        var rule = new WatchRule { Category = ProductCategory.Concentrate, StoreKeys = { "trulieve:marietta" }, MaxPrice = 60 };
        Assert.True(WatchMatcher.Matches(rule, Event(ChangeKind.BackInStock)));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.BackInStock, category: ProductCategory.Vape)));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.BackInStock, store: "trulieve:dunwoody_ga")));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.BackInStock, price: 75)));
    }

    [Fact]
    public void Event_kinds_follow_the_rule_toggles()
    {
        var rule = new WatchRule { NotifyAvailable = true, NotifyRestock = true, NotifySoldOut = false, NotifyPriceDrop = false };
        Assert.True(WatchMatcher.Matches(rule, Event(ChangeKind.NewProduct)));
        Assert.True(WatchMatcher.Matches(rule, Event(ChangeKind.Restocked)));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.SoldOut)));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.PriceDrop)));
        Assert.False(WatchMatcher.Matches(new WatchRule { Enabled = false }, Event(ChangeKind.NewProduct)));
    }

    [Fact]
    public void Low_stock_fires_once_when_crossing_the_threshold()
    {
        var rule = new WatchRule { LowStockThreshold = 5 };
        Assert.True(WatchMatcher.Matches(rule, Event(ChangeKind.QuantityChanged, oldQty: 8, newQty: 4)));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.QuantityChanged, oldQty: 4, newQty: 3)));
        Assert.False(WatchMatcher.Matches(rule, Event(ChangeKind.QuantityChanged, oldQty: 8, newQty: 0)));
    }
}

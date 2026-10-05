using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public sealed class PriceHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gci-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Key = "trulieve:marietta|123|default";

    private static ChangeEvent Price(DateTimeOffset at, decimal old, decimal @new, string key = Key) => new()
    {
        At = at,
        Kind = @new < old ? ChangeKind.PriceDrop : ChangeKind.PriceIncrease,
        StoreKey = "trulieve:marietta",
        StoreName = "Trulieve Marietta",
        Operator = "Trulieve",
        ItemKey = key,
        Name = "Northern Lights Vape Cart",
        OldPrice = old,
        NewPrice = @new,
    };

    private static PriceHistory Since(int daysAgo, params ChangeEvent[] events)
    {
        var h = new PriceHistory { Since = Now.AddDays(-daysAgo) };
        h.Record(events);
        return h;
    }

    [Fact]
    public void No_history_means_no_signal()
    {
        Assert.Null(Since(30).StatsFor(Key, 40m, Now));
    }

    [Fact]
    public void A_lasting_drop_is_below_usual_and_the_30_day_low()
    {
        var stats = Since(20, Price(Now.AddDays(-3), 40m, 35m)).StatsFor(Key, 35m, Now)!;

        Assert.Equal(40m, stats.Usual);
        Assert.True(stats.Below);
        Assert.True(stats.Lowest30);
        Assert.False(stats.Above);
        Assert.Equal(13, stats.Percent);
        Assert.Equal(35m, stats.Low30);
        Assert.Equal(40m, stats.High);
    }

    [Fact]
    public void Once_a_new_price_has_held_longest_it_becomes_usual()
    {
        var stats = Since(20, Price(Now.AddDays(-15), 40m, 35m)).StatsFor(Key, 35m, Now)!;

        Assert.Equal(35m, stats.Usual);
        Assert.False(stats.Below);
        Assert.True(stats.Lowest30); // still the cheapest it has been in 30 days
    }

    [Fact]
    public void An_increase_reads_as_above_usual()
    {
        var stats = Since(20, Price(Now.AddDays(-2), 50m, 60m)).StatsFor(Key, 60m, Now)!;

        Assert.True(stats.Above);
        Assert.Equal(50m, stats.Usual);
        Assert.Equal(20, stats.Percent);
        Assert.False(stats.Lowest30);
    }

    [Fact]
    public void A_glitch_that_reverts_within_an_hour_is_ignored()
    {
        // The Sep 27 pattern: every price dips and comes back ten minutes later.
        var history = Since(20, Price(Now.AddDays(-8), 10m, 8m), Price(Now.AddDays(-8).AddMinutes(10), 8m, 10m));

        Assert.Null(history.StatsFor(Key, 10m, Now));
    }

    [Fact]
    public void Small_moves_under_two_dollars_and_five_percent_are_not_flagged()
    {
        var stats = Since(20, Price(Now.AddDays(-2), 45m, 44m)).StatsFor(Key, 44m, Now)!;

        Assert.False(stats.Below);
        Assert.False(stats.Above);
    }

    [Fact]
    public void The_window_never_reaches_back_before_recording_began()
    {
        // Only 4 days observed: 1 day at $63 before the drop, 3 at $55 — so $55 is already "usual".
        var stats = Since(4, Price(Now.AddDays(-3), 63m, 55m)).StatsFor(Key, 55m, Now)!;

        Assert.Equal(55m, stats.Usual);
        Assert.True(stats.Lowest30);
    }

    [Fact]
    public void Recording_ignores_other_events_and_duplicates()
    {
        var h = new PriceHistory { Since = Now.AddDays(-10) };
        var drop = Price(Now.AddDays(-1), 40m, 35m);
        var soldOut = new ChangeEvent
        {
            At = Now, Kind = ChangeKind.SoldOut, StoreKey = "s", StoreName = "S", Operator = "O", ItemKey = Key, Name = "X", OldPrice = 35m,
        };

        Assert.True(h.Record([drop, soldOut]));
        Assert.False(h.Record([drop])); // seeding overlap: same change again
        Assert.Single(h.Items[Key]);
    }

    [Fact]
    public void Prune_drops_points_older_than_the_retention()
    {
        var h = Since(400, Price(Now.AddDays(-200), 40m, 35m), Price(Now.AddDays(-10), 35m, 30m));
        h.Prune(Now);

        Assert.Single(h.Items[Key]);
        h.Items[Key].Clear();
        h.Record([Price(Now.AddDays(-300), 1m, 2m)]);
        h.Prune(Now);
        Assert.False(h.Items.ContainsKey(Key));
    }

    [Fact]
    public void First_load_seeds_from_the_change_log_and_later_loads_read_the_file()
    {
        var data = new DataStore(_root);
        var seeded = PriceHistory.Load(data, [Price(Now.AddDays(-5), 40m, 35m)], Now);
        Assert.Equal(Now.AddDays(-5), seeded.Since);
        Assert.Single(seeded.Items[Key]);
        seeded.Save(data);

        var reloaded = PriceHistory.Load(data, [], Now);
        Assert.Equal(seeded.Since, reloaded.Since);
        Assert.Equal(35m, reloaded.Items[Key][0].New);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}

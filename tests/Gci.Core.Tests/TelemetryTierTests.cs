using System.Text.Json.Nodes;
using Gci.Core.Services;

namespace Gci.Core.Tests;

/// <summary>The two-tier split: the basic install ping sends without opt-in; detailed usage waits for consent.</summary>
public sealed class TelemetryTierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gci-tier", Guid.NewGuid().ToString("N"));
    private readonly FakeAptabase _server = new();
    private readonly DateTimeOffset _now = new(2026, 9, 14, 14, 0, 0, TimeSpan.Zero);
    private static readonly TelemetrySystemInfo System = new("10.0.26100", "en-US", "1.3.1", "abc1234", "X64", IsDebug: true);

    private TelemetryClient New(bool basic, bool detailed) =>
        new(new DataStore(_root), System, TelemetryClient.DefaultAppKey, _server, () => _now)
        {
            BasicEnabled = basic,
            DetailedEnabled = detailed,
        };

    private List<string> SentEventNames() => _server.Requests
        .SelectMany(r => JsonNode.Parse(r.Body)!.AsArray())
        .Select(e => e!["eventName"]!.GetValue<string>())
        .ToList();

    [Fact]
    public async Task Basic_ping_sends_without_opt_in_but_detailed_events_do_not()
    {
        using var client = New(basic: true, detailed: false);

        client.TrackBasic("app_started", new Dictionary<string, object?> { ["install_id"] = "abc" });
        client.Track("tab_viewed", new Dictionary<string, object?> { ["tab"] = "inventory" }); // detailed, dropped
        client.Increment("refreshes");                                                          // detailed, dropped
        await client.FlushAsync();

        Assert.Equal(["app_started"], SentEventNames());
    }

    [Fact]
    public async Task Opting_into_detailed_lets_both_tiers_through()
    {
        using var client = New(basic: true, detailed: true);

        client.TrackBasic("app_active");
        client.Track("preorder_opened", new Dictionary<string, object?> { ["platform"] = "Trulieve" });
        await client.FlushAsync();

        Assert.Equal(["app_active", "preorder_opened"], SentEventNames().Order().ToList());
    }

    [Fact]
    public async Task Turning_basic_off_stops_the_ping_and_drops_queued_basic_events()
    {
        using var client = New(basic: true, detailed: false);
        client.TrackBasic("app_started");

        client.BasicEnabled = false;   // opting out drops what's queued
        client.TrackBasic("app_active"); // and stops new pings
        await client.FlushAsync();

        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Turning_detailed_off_keeps_a_queued_basic_ping()
    {
        using var client = New(basic: true, detailed: true);
        client.TrackBasic("app_started");
        client.Track("tab_viewed");

        client.DetailedEnabled = false; // drops the detailed event, keeps the basic ping
        await client.FlushAsync();

        Assert.Equal(["app_started"], SentEventNames());
    }

    [Fact]
    public async Task With_no_tier_on_nothing_is_sent()
    {
        using var client = New(basic: false, detailed: false);
        client.TrackBasic("app_started");
        client.Track("tab_viewed");
        await client.FlushAsync();

        Assert.Empty(_server.Requests);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

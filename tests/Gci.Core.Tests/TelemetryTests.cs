using System.Net;
using System.Text.Json.Nodes;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public sealed class TelemetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gci-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAptabase _server = new();
    private DateTimeOffset _now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);
    private static readonly TelemetrySystemInfo System = new("10.0.26100", "en-US", "1.1.0", "abc1234", "X64", IsDebug: true);

    private TelemetryClient NewClient(bool enabled = true) =>
        new(new DataStore(_root), System, TelemetryClient.DefaultAppKey, _server, () => _now) { Enabled = enabled };

    [Fact]
    public async Task Nothing_is_queued_or_sent_without_consent()
    {
        using var client = NewClient(enabled: false);
        client.Track("app_started");
        client.Increment("refreshes");
        client.TrackError(new InvalidOperationException("boom"), "handled");
        client.Enabled = true;
        await client.FlushAsync();

        Assert.Empty(_server.Requests);
        Assert.Empty(client.TakeCounters());
    }

    [Fact]
    public async Task Events_are_batched_in_aptabase_format()
    {
        using var client = NewClient();
        client.Track("app_started", new Dictionary<string, object?> { ["launch"] = "normal", ["watches"] = 3, ["images"] = true });
        await client.FlushAsync();

        var request = Assert.Single(_server.Requests);
        Assert.Equal("https://us.aptabase.com/api/v0/events", request.Url);
        Assert.Equal("A-US-1722619423", request.AppKey);
        var e = JsonNode.Parse(request.Body)!.AsArray().Single()!;
        Assert.Equal("app_started", e["eventName"]!.GetValue<string>());
        Assert.True(ulong.TryParse(e["sessionId"]!.GetValue<string>(), out _));
        Assert.Equal("Windows", e["systemProps"]!["osName"]!.GetValue<string>());
        Assert.True(e["systemProps"]!["isDebug"]!.GetValue<bool>());
        Assert.Equal("1.1.0", e["systemProps"]!["appVersion"]!.GetValue<string>());
        Assert.Equal(3, e["props"]!["watches"]!.GetValue<double>());
        Assert.True(e["props"]!["images"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Large_queues_are_sent_25_at_a_time()
    {
        using var client = NewClient();
        for (var i = 0; i < 60; i++) client.Track("tick", new Dictionary<string, object?> { ["i"] = i });
        await client.FlushAsync();

        Assert.Equal(new[] { 25, 25, 10 }, _server.Requests.Select(r => JsonNode.Parse(r.Body)!.AsArray().Count));
    }

    [Fact]
    public async Task Server_errors_keep_events_for_later_and_rejections_drop_them()
    {
        using var client = NewClient();
        client.Track("a");
        _server.Status = HttpStatusCode.ServiceUnavailable;
        await client.FlushAsync();
        _server.Status = HttpStatusCode.OK;
        await client.FlushAsync();
        Assert.Equal(2, _server.Requests.Count); // retried

        client.Track("b");
        _server.Status = HttpStatusCode.BadRequest;
        await client.FlushAsync();
        _server.Status = HttpStatusCode.OK;
        await client.FlushAsync();
        Assert.Equal(3, _server.Requests.Count); // dropped, not retried
    }

    [Fact]
    public async Task Queue_survives_restart_but_stale_events_are_dropped()
    {
        using (var first = NewClient())
        {
            _server.Status = HttpStatusCode.ServiceUnavailable;
            first.Track("queued_while_offline");
        }
        _server.Requests.Clear();
        _server.Status = HttpStatusCode.OK;

        using (var second = NewClient())
        {
            await second.FlushAsync();
            Assert.Contains("queued_while_offline", Assert.Single(_server.Requests).Body);
        }

        using (var third = NewClient())
        {
            _server.Status = HttpStatusCode.ServiceUnavailable;
            third.Track("too_old");
            await third.FlushAsync();
            _server.Requests.Clear();
            _server.Status = HttpStatusCode.OK;
            _now += TimeSpan.FromHours(30); // Aptabase rejects events older than a day
            await third.FlushAsync();
            Assert.Empty(_server.Requests);
        }
    }

    [Fact]
    public async Task Turning_telemetry_off_discards_the_queue()
    {
        using var client = NewClient();
        client.Track("pending");
        client.Enabled = false;
        client.Enabled = true;
        await client.FlushAsync();
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Daily_cap_limits_volume_and_counts_what_was_dropped()
    {
        using var client = NewClient();
        for (var i = 0; i < TelemetryClient.DailyEventCap + 7; i++) client.Track("spam");
        await client.FlushAsync();

        Assert.Equal(TelemetryClient.DailyEventCap, _server.Requests.Sum(r => JsonNode.Parse(r.Body)!.AsArray().Count));
        Assert.Equal(7.0, client.TakeCounters()["dropped_events"]);
    }

    [Fact]
    public void Once_and_limited_events_are_throttled()
    {
        using var client = NewClient();
        Assert.True(client.TrackOnce("store:x", TimeSpan.FromDays(7), "store_seen"));
        Assert.False(client.TrackOnce("store:x", TimeSpan.FromDays(7), "store_seen"));
        _now += TimeSpan.FromDays(8);
        Assert.True(client.TrackOnce("store:x", TimeSpan.FromDays(7), "store_seen"));

        Assert.True(client.TrackLimited("product_opened", 2));
        Assert.True(client.TrackLimited("product_opened", 2));
        Assert.False(client.TrackLimited("product_opened", 2));
        Assert.Equal(1.0, client.TakeCounters()["over_limit_product_opened"]);
    }

    [Fact]
    public async Task Errors_go_to_the_error_endpoint_scrubbed_and_deduplicated()
    {
        using var client = NewClient();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ex = Throw(() => throw new IOException($@"Can't open {profile}\AppData\Local\GCI\state.json for someone@example.com"));
        client.TrackError(ex, "unhandled");
        client.TrackError(ex, "unhandled"); // same signature, same day
        await client.FlushAsync();

        var request = Assert.Single(_server.Requests);
        Assert.Equal("https://us.aptabase.com/api/v0/error", request.Url);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("IOException", body["errorType"]!.GetValue<string>());
        Assert.DoesNotContain(profile, body["errorMessage"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", body["errorMessage"]!.GetValue<string>());
        Assert.Contains("<email>", body["errorMessage"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Card 12345678 on file", "Card <number> on file")]
    [InlineData("call 678-335-4441", "call <number>")]
    [InlineData("https://ntfy.sh/gci-james-4821 failed", "<ntfy-topic> failed")]
    [InlineData("Riddler Crumble 1g $50", "Riddler Crumble 1g $50")]
    [InlineData("Grape Cake #8 Flower 3.5g", "Grape Cake #8 Flower 3.5g")]
    public void Scrubber_removes_identifying_text_but_keeps_product_names(string input, string expected) =>
        Assert.Equal(expected, TelemetryScrubber.Text(input, 180));

    [Fact]
    public void Scrubber_normalizes_keys_and_truncates_values()
    {
        var props = TelemetryScrubber.Props(new Dictionary<string, object?>
        {
            ["Store: Lotus Farmacy - Suwanee (Botanical Sciences) extra long"] = 1,
            ["long"] = new string('x', 500),
            ["nothing"] = null,
        })!;
        Assert.All(props.Keys, k => Assert.True(k.Length <= 40 && k.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')));
        Assert.Equal(180, ((string)props["long"]).Length);
        Assert.False(props.ContainsKey("nothing"));
    }

    [Fact]
    public void Only_known_product_terms_are_reported_from_keywords()
    {
        var (recognized, custom) = KeywordVocabulary.Classify(new[] { "Crumble", "live rosin", "my secret stash", "Riddler", "" });
        Assert.Equal(new[] { "crumble", "live rosin" }, recognized);
        Assert.Equal(2, custom);
    }

    [Fact]
    public void Invalid_app_keys_disable_sending()
    {
        using var client = new TelemetryClient(new DataStore(_root), System, "not-a-key", _server) { Enabled = true };
        Assert.False(client.IsConfigured);
        Assert.False(client.Enabled);
    }

    [Fact]
    public async Task Sent_events_are_logged_locally_for_the_user()
    {
        using var client = NewClient();
        client.Track("app_started", new Dictionary<string, object?> { ["launch"] = "normal" });
        await client.FlushAsync();
        var line = Assert.Single(File.ReadAllLines(client.LogPath));
        Assert.Contains("\"event\":\"app_started\"", line);
        Assert.Contains("\"status\":200", line);
    }

    private static Exception Throw(Action a)
    {
        try { a(); } catch (Exception e) { return e; }
        throw new InvalidOperationException("expected an exception");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeAptabase : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<(string Url, string? AppKey, string Body, HttpStatusCode Status)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var key = request.Headers.TryGetValues("App-Key", out var v) ? v.First() : null;
            lock (Requests) Requests.Add((request.RequestUri!.ToString(), key, body, Status));
            return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
        }
    }
}

using System.Net;
using System.Text.Json;

namespace Gci.Core.Services;

/// <summary>A sponsor message shown in the app header for awareness. Hardware/accessory brands only
/// (federally legal e-commerce), never a cannabis product. The <see cref="Text"/> is the ready-to-show line.</summary>
public sealed record Sponsor(string Id, string Brand, string Text, string Url);

/// <summary>
/// Fetches the optional sponsor feed — a tiny ads.json served from GCI's own CloudFront distribution — and picks the
/// sponsor to show now. The last successful fetch is cached on disk, so the header is populated instantly and works
/// offline; it shows nothing when the feed is empty, unreachable, or the user has turned sponsors off. The feed is
/// GCI-authored (not third-party markup), and only https links are ever surfaced.
/// </summary>
public sealed class SponsorService : IDisposable
{
    public const string FileName = "sponsors.json";
    public const string DefaultFeedUrl = "https://gcimenu.com/ads.json";

    private readonly DataStore _data;
    private readonly HttpClient _http;
    private readonly string _feedUrl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private List<SponsorEntry> _cache;

    public SponsorService(DataStore data, HttpMessageHandler? handler = null, string? userAgent = null,
        string? feedUrl = null, Func<DateTimeOffset>? clock = null)
    {
        _data = data;
        _feedUrl = feedUrl ?? DefaultFeedUrl;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? "GCI/1.0");
        _cache = data.Load(FileName, () => new List<SponsorEntry>());
    }

    /// <summary>
    /// Fetches the feed and returns the sponsor to show now (weighted-random among those inside their active window),
    /// or null when there's nothing to show. Never throws — a failed fetch quietly keeps the last cached feed.
    /// </summary>
    public async Task<Sponsor?> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(_feedUrl, ct);
            var feed = JsonSerializer.Deserialize<SponsorFeed>(json, DataStore.Options);
            if (feed?.Sponsors is { } list)
            {
                lock (_lock) _cache = list;
                _data.Save(FileName, list);
            }
        }
        catch (Exception)
        {
            // Offline, timed out, or a malformed feed: fall back to whatever was cached last.
        }
        return Current();
    }

    /// <summary>The sponsor to show from the cached feed, chosen without a network call.</summary>
    public Sponsor? Current()
    {
        List<SponsorEntry> active;
        lock (_lock)
        {
            var now = _clock();
            active = _cache.Where(s => s.IsActive(now)).ToList();
        }
        if (active.Count == 0) return null;
        var pick = WeightedPick(active);
        return new Sponsor(pick.Id ?? "", pick.Brand ?? "", pick.Text ?? "", pick.Url ?? "");
    }

    private static SponsorEntry WeightedPick(List<SponsorEntry> active)
    {
        var total = active.Sum(s => Math.Max(1, s.Weight));
        var r = Random.Shared.Next(total);
        foreach (var s in active)
        {
            r -= Math.Max(1, s.Weight);
            if (r < 0) return s;
        }
        return active[0];
    }

    public void Dispose() => _http.Dispose();

    private sealed class SponsorFeed
    {
        public List<SponsorEntry>? Sponsors { get; set; }
    }

    public sealed class SponsorEntry
    {
        public string? Id { get; set; }
        public string? Brand { get; set; }
        public string? Text { get; set; }
        public string? Url { get; set; }
        /// <summary>Optional window: the sponsor shows only from Start (if set) through End (if set).</summary>
        public DateTimeOffset? Start { get; set; }
        public DateTimeOffset? End { get; set; }
        /// <summary>Relative rotation weight among concurrently active sponsors (defaults to 1).</summary>
        public int Weight { get; set; } = 1;

        /// <summary>Active when it has a showable message and an https link, and now is inside its window.</summary>
        public bool IsActive(DateTimeOffset now) =>
            !string.IsNullOrWhiteSpace(Text)
            && Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && (Start is not { } s || now >= s)
            && (End is not { } e || now <= e);
    }
}

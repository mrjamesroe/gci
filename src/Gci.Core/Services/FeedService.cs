using System.Net;
using Gci.Core.Models;

namespace Gci.Core.Services;

public sealed record FeedStatus(DateTimeOffset? LastChecked, DateTimeOffset? LastSuccess, string? Error, string? ETag, DateTimeOffset? LastModified);

/// <summary>
/// Follows RSS/Atom feeds (The Peach Scout by default) and reports posts it hasn't seen before.
/// A feed's first successful read is a silent baseline (existing posts show up as already read);
/// a failed read keeps the posts GCI already has.
/// </summary>
public sealed class FeedService : IDisposable
{
    public const string FileName = "feeds.json";
    public const string PeachScoutUrl = "https://www.peachscout.com/blog-feed.xml";
    private const int MaxPostsPerFeed = 150;

    private readonly DataStore _data;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly State _state;

    public FeedService(DataStore data, HttpMessageHandler? handler = null, string? userAgent = null, Func<DateTimeOffset>? clock = null)
    {
        _data = data;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? "GCI/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.5");
        _state = data.Load(FileName, () => new State
        {
            Sources = { new FeedSource { Id = "peachscout", Name = "The Peach Scout", Url = PeachScoutUrl } },
        });
    }

    public IReadOnlyList<FeedSource> Sources { get { lock (_lock) return _state.Sources.ToList(); } }

    /// <summary>Posts from enabled feeds, newest first.</summary>
    public IReadOnlyList<FeedPost> Posts
    {
        get
        {
            lock (_lock)
            {
                var enabled = _state.Sources.Where(s => s.Enabled).Select(s => s.Id).ToHashSet();
                return _state.Posts.Where(p => enabled.Contains(p.FeedId)).OrderByDescending(p => p.SortDate).ToList();
            }
        }
    }

    public int UnreadCount { get { lock (_lock) return Posts.Count(p => !_state.Read.Contains(p.Key)); } }

    public bool IsRead(FeedPost post) { lock (_lock) return _state.Read.Contains(post.Key); }

    public FeedStatus? GetStatus(string feedId) { lock (_lock) return _state.Status.GetValueOrDefault(feedId); }

    public void MarkRead(FeedPost post)
    {
        lock (_lock)
        {
            if (_state.Read.Add(post.Key)) Save();
        }
    }

    public void MarkAllRead()
    {
        lock (_lock)
        {
            foreach (var p in _state.Posts) _state.Read.Add(p.Key);
            Save();
        }
    }

    public FeedSource AddSource(string url, string? name = null)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter a full http(s) feed URL.");
        lock (_lock)
        {
            if (_state.Sources.FirstOrDefault(s => string.Equals(s.Url, uri.ToString(), StringComparison.OrdinalIgnoreCase)) is { } existing)
                return existing;
            var source = new FeedSource { Name = name ?? uri.Host, Url = uri.ToString() };
            _state.Sources.Add(source);
            Save();
            return source;
        }
    }

    public void RemoveSource(string feedId)
    {
        lock (_lock)
        {
            _state.Sources.RemoveAll(s => s.Id == feedId);
            _state.Posts.RemoveAll(p => p.FeedId == feedId);
            _state.Status.Remove(feedId);
            Save();
        }
    }

    public void UpdateSource(string feedId, bool enabled, bool notify)
    {
        lock (_lock)
        {
            if (_state.Sources.FirstOrDefault(s => s.Id == feedId) is not { } s) return;
            s.Enabled = enabled;
            s.Notify = notify;
            Save();
        }
    }

    /// <summary>Reads every enabled feed; returns posts never seen before (baseline reads return none).</summary>
    public async Task<IReadOnlyList<FeedPost>> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var fresh = new List<FeedPost>();
            foreach (var source in Sources.Where(s => s.Enabled))
                fresh.AddRange(await RefreshFeedAsync(source, ct));
            lock (_lock) Save();
            return fresh;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<FeedPost>> RefreshFeedAsync(FeedSource source, CancellationToken ct)
    {
        var now = _clock();
        FeedStatus? prior;
        lock (_lock) prior = _state.Status.GetValueOrDefault(source.Id);

        ParsedFeed parsed;
        string? etag;
        DateTimeOffset? lastModified;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, source.Url);
            if (prior?.ETag is { } e) req.Headers.TryAddWithoutValidation("If-None-Match", e);
            if (prior?.LastModified is { } lm) req.Headers.IfModifiedSince = lm;
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotModified)
            {
                SetStatus(source.Id, prior! with { LastChecked = now, LastSuccess = now, Error = null });
                return [];
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
            parsed = FeedParser.Parse(await res.Content.ReadAsStringAsync(ct));
            etag = res.Headers.ETag?.ToString();
            lastModified = res.Content.Headers.LastModified;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(source.Id, new FeedStatus(now, prior?.LastSuccess, ex.Message, prior?.ETag, prior?.LastModified));
            return [];
        }

        var fresh = new List<FeedPost>();
        lock (_lock)
        {
            var baseline = prior?.LastSuccess is null;
            var known = _state.Posts.Where(p => p.FeedId == source.Id).Select(p => p.PostId).ToHashSet();
            foreach (var item in parsed.Posts.Where(p => !known.Contains(p.Id)))
            {
                var post = new FeedPost
                {
                    FeedId = source.Id,
                    PostId = item.Id,
                    Title = item.Title,
                    Link = item.Link,
                    Summary = item.Summary,
                    PublishedAt = item.PublishedAt,
                    Categories = item.Categories.ToList(),
                    ImageUrl = item.ImageUrl,
                    ThumbnailUrl = item.ThumbnailUrl,
                    Author = item.Author,
                    FirstSeenAt = now,
                };
                _state.Posts.Add(post);
                if (baseline) _state.Read.Add(post.Key);
                else fresh.Add(post);
            }

            if (source.Name == new Uri(source.Url).Host && parsed.Title is { } title) source.Name = title;
            var mine = _state.Posts.Where(p => p.FeedId == source.Id).OrderByDescending(p => p.SortDate).ToList();
            foreach (var old in mine.Skip(MaxPostsPerFeed))
            {
                _state.Posts.Remove(old);
                _state.Read.Remove(old.Key);
            }
            _state.Status[source.Id] = new FeedStatus(now, now, null, etag, lastModified);
        }
        return fresh;
    }

    private void SetStatus(string feedId, FeedStatus status)
    {
        lock (_lock) _state.Status[feedId] = status;
    }

    private void Save() => _data.Save(FileName, _state);

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }

    internal sealed class State
    {
        public List<FeedSource> Sources { get; set; } = new();
        public List<FeedPost> Posts { get; set; } = new();
        public HashSet<string> Read { get; set; } = new();
        public Dictionary<string, FeedStatus> Status { get; set; } = new();
    }
}

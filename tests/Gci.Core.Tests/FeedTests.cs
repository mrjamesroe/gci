using System.Net;
using System.Text;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public sealed class FeedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gci-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeFeedServer _server = new();
    private readonly FeedService _feeds;

    public FeedTests()
    {
        _feeds = new FeedService(new DataStore(_root), _server);
    }

    // Shaped like a Wix blog feed (what The Peach Scout publishes), with invented content.
    private static string Rss(params (string Id, string Title, string Date)[] items) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <channel>
            <title>Test Scout</title>
            {string.Concat(items.Select(i => $"""
            <item>
              <title><![CDATA[{i.Title}]]></title>
              <description><![CDATA[<p>Patients report the <b>new batch</b> sold out &amp; restocks are expected.</p>]]></description>
              <link>https://example.test/post/{i.Id}</link>
              <guid isPermaLink="false">{i.Id}</guid>
              <category><![CDATA[Trulieve]]></category>
              <pubDate>{i.Date}</pubDate>
              <enclosure url="https://static.wixstatic.com/media/abc~mv2.jpeg/v1/fit/w_1000,h_1000,al_c,q_80/file.png" length="0" type="image/png"/>
              <dc:creator><![CDATA[Scout]]></dc:creator>
            </item>
            """))}
          </channel>
        </rss>
        """;

    [Fact]
    public void Parses_wix_style_rss()
    {
        var feed = FeedParser.Parse(Rss(("a1", "Crumble: “Test” from Trulieve", "Sat, 22 Aug 2026 18:27:43 GMT")));

        Assert.Equal("Test Scout", feed.Title);
        var post = Assert.Single(feed.Posts);
        Assert.Equal(("a1", "Crumble: “Test” from Trulieve", "https://example.test/post/a1"), (post.Id, post.Title, post.Link));
        Assert.Equal("Patients report the new batch sold out & restocks are expected.", post.Summary);
        Assert.Equal(new DateTimeOffset(2026, 8, 22, 18, 27, 43, TimeSpan.Zero), post.PublishedAt);
        Assert.Equal(new[] { "Trulieve" }, post.Categories);
        Assert.EndsWith("w_1000,h_1000,al_c,q_80/file.png", post.ImageUrl);
        Assert.Contains("w_320,h_320", post.ThumbnailUrl);
        Assert.Equal("Scout", post.Author);
    }

    [Fact]
    public void Parses_atom()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom News</title>
              <entry>
                <title>Commission posts testing rules</title>
                <id>tag:example,2026:1</id>
                <link rel="alternate" href="https://example.test/rules" />
                <updated>2026-09-01T12:00:00Z</updated>
                <summary>Draft &lt;b&gt;concentrate&lt;/b&gt; testing standards.</summary>
                <category term="GMCC" />
              </entry>
            </feed>
            """;
        var post = Assert.Single(FeedParser.Parse(atom).Posts);
        Assert.Equal(("tag:example,2026:1", "https://example.test/rules", "Draft concentrate testing standards."), (post.Id, post.Link, post.Summary));
        Assert.Equal(new[] { "GMCC" }, post.Categories);
    }

    [Fact]
    public void Long_summaries_are_trimmed_at_a_word()
    {
        var text = string.Join(' ', Enumerable.Repeat("crumble", 100));
        var excerpt = FeedParser.Excerpt(text)!;
        Assert.True(excerpt.Length <= 321);
        Assert.EndsWith("crumble…", excerpt);
    }

    [Fact]
    public async Task First_read_is_a_silent_baseline_then_new_posts_are_reported_unread()
    {
        _server.Body = Rss(("a1", "Older post", "Tue, 18 Aug 2026 03:07:52 GMT"));
        Assert.Empty(await _feeds.RefreshAsync());
        Assert.Equal(0, _feeds.UnreadCount);
        Assert.Single(_feeds.Posts);

        _server.Body = Rss(("a2", "Trulieve Now Offering Crumble", "Thu, 20 Aug 2026 17:09:06 GMT"),
                           ("a1", "Older post", "Tue, 18 Aug 2026 03:07:52 GMT"));
        var fresh = await _feeds.RefreshAsync();

        Assert.Equal("Trulieve Now Offering Crumble", Assert.Single(fresh).Title);
        Assert.Equal(1, _feeds.UnreadCount);
        Assert.Equal(new[] { "a2", "a1" }, _feeds.Posts.Select(p => p.PostId));

        _feeds.MarkRead(fresh[0]);
        Assert.Equal(0, _feeds.UnreadCount);
        Assert.Empty(await _feeds.RefreshAsync());
    }

    [Fact]
    public async Task Failed_read_keeps_existing_posts_and_records_the_error()
    {
        _server.Body = Rss(("a1", "Older post", "Tue, 18 Aug 2026 03:07:52 GMT"));
        await _feeds.RefreshAsync();

        _server.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Empty(await _feeds.RefreshAsync());
        Assert.Single(_feeds.Posts);
        Assert.Contains("503", _feeds.GetStatus("peachscout")!.Error);

        _server.Status = HttpStatusCode.OK;
        _server.Body = "<html>not a feed</html>";
        Assert.Empty(await _feeds.RefreshAsync());
        Assert.Single(_feeds.Posts);
    }

    [Fact]
    public async Task Conditional_requests_are_used_when_the_feed_sends_validators()
    {
        _server.Body = Rss(("a1", "Older post", "Tue, 18 Aug 2026 03:07:52 GMT"));
        _server.ETag = "\"f1\"";
        await _feeds.RefreshAsync();
        await _feeds.RefreshAsync();

        Assert.Equal(HttpStatusCode.NotModified, _server.LastStatus);
        Assert.Null(_feeds.GetStatus("peachscout")!.Error);
    }

    [Fact]
    public async Task State_persists_across_restarts()
    {
        _server.Body = Rss(("a1", "Older post", "Tue, 18 Aug 2026 03:07:52 GMT"));
        await _feeds.RefreshAsync();
        _server.Body = Rss(("a2", "New post", "Thu, 20 Aug 2026 17:09:06 GMT"), ("a1", "Older post", "Tue, 18 Aug 2026 03:07:52 GMT"));
        await _feeds.RefreshAsync();

        using var reopened = new FeedService(new DataStore(_root), _server);
        Assert.Equal(1, reopened.UnreadCount);
        Assert.Empty(await reopened.RefreshAsync());
    }

    [Fact]
    public void Added_feeds_must_be_http_urls_and_are_deduplicated()
    {
        Assert.Throws<ArgumentException>(() => _feeds.AddSource("not a url"));
        var a = _feeds.AddSource("https://example.test/feed.xml");
        var b = _feeds.AddSource("https://EXAMPLE.test/feed.xml");
        Assert.Same(a, b);
        Assert.Equal(2, _feeds.Sources.Count);
        _feeds.RemoveSource(a.Id);
        Assert.Single(_feeds.Sources);
    }

    [Fact]
    public void Posts_match_watches_by_keyword()
    {
        var post = new FeedPost
        {
            FeedId = "f", PostId = "1", Title = "Trulieve Now Offering Georgia’s First Crumble Concentrate",
            Summary = "Sold out statewide.", Categories = { "Trulieve" },
        };
        Assert.True(WatchMatcher.Matches(new WatchRule { Keywords = { "crumble", "live rosin" } }, post));
        Assert.True(WatchMatcher.Matches(new WatchRule { Keywords = { "crumble" }, Operator = "Trulieve" }, post));
        Assert.False(WatchMatcher.Matches(new WatchRule { Keywords = { "crumble" }, Operator = "Fine Fettle" }, post));
        Assert.False(WatchMatcher.Matches(new WatchRule { Category = ProductCategory.Concentrate }, post)); // no keywords
    }

    public void Dispose()
    {
        _feeds.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeFeedServer : HttpMessageHandler
    {
        public string Body { get; set; } = "";
        public string? ETag { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode LastStatus { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var v) ? v.First() : null;
            var res = ETag is not null && ifNoneMatch == ETag && Status == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "text/xml") };
            if (ETag is not null && res.StatusCode == HttpStatusCode.OK) res.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(ETag);
            LastStatus = res.StatusCode;
            return Task.FromResult(res);
        }
    }
}

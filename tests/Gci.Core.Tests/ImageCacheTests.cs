using System.Net;
using System.Text;
using Gci.Core.Services;

namespace Gci.Core.Tests;

public sealed class ImageCacheTests : IDisposable
{
    private const string Source = "https://img.test/photos/a.png";
    private const string Thumb = "https://cdn.test/resize/w=240/photos/a.png";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gci-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeImageServer _server = new();
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly ImageCache _cache;
    private readonly List<string> _changed = new();

    public ImageCacheTests()
    {
        // Identity "thumbnailer": the stored file is exactly the bytes that were used, so tests can see which source won.
        _cache = new ImageCache(_root, bytes => bytes, _server, clock: () => _now);
        _cache.ImageChanged += url => { lock (_changed) _changed.Add(url); };
    }

    private static readonly ImageRef Image = new("store|p1|default", Source, Thumb);

    private static byte[] Png(string tag) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.ASCII.GetBytes($"-{tag}-padding")];

    private string StoredContent() => Encoding.ASCII.GetString(File.ReadAllBytes(_cache.GetThumbnailPath(Source)!)[8..]);

    private async Task SeedAsync(bool clearLog = true)
    {
        _server.Serve(Source, Png("original-v1"), etag: "\"v1\"");
        _server.Serve(Thumb, Png("thumb-v1"));
        Assert.Equal(ImageOutcome.Fetched, await _cache.EnsureAsync(Image));
        if (!clearLog) return;
        _server.Requests.Clear();
        _changed.Clear();
    }

    [Fact]
    public async Task First_fetch_reads_validators_from_the_original_and_bytes_from_a_versioned_thumbnail()
    {
        await SeedAsync(clearLog: false);

        Assert.Equal("-thumb-v1-padding", StoredContent());
        Assert.Equal(new[] { Source }, _changed);
        var entry = Assert.Single(_server.Log, r => r.Method == "GET");
        Assert.StartsWith(Thumb + "?v=", entry.Url);
        Assert.Contains(_server.Log, r => r.Method == "HEAD" && r.Url == Source);
    }

    [Fact]
    public async Task Fresh_thumbnails_make_no_requests()
    {
        await SeedAsync();
        _now += TimeSpan.FromHours(11);

        Assert.Equal(ImageOutcome.Unchanged, await _cache.EnsureAsync(Image));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Stale_thumbnail_is_confirmed_with_a_conditional_request_on_the_original()
    {
        await SeedAsync();
        _now += TimeSpan.FromHours(13);

        Assert.Equal(ImageOutcome.Unchanged, await _cache.EnsureAsync(Image));

        var request = Assert.Single(_server.Requests);
        Assert.Equal((Source, "\"v1\"", HttpStatusCode.NotModified), (request.Url, request.IfNoneMatch, request.Status));
        Assert.Equal("-thumb-v1-padding", StoredContent());
        Assert.Empty(_changed);
    }

    [Fact]
    public async Task Changed_original_is_rebuilt_from_the_original_even_if_the_cdn_copy_is_stale()
    {
        await SeedAsync();
        _server.Serve(Source, Png("original-v2"), etag: "\"v2\""); // the CDN at Thumb still serves thumb-v1
        _now += TimeSpan.FromHours(13);

        Assert.Equal(ImageOutcome.Changed, await _cache.EnsureAsync(Image));

        Assert.Equal("-original-v2-padding", StoredContent());
        Assert.Equal(new[] { Source }, _changed);
        Assert.DoesNotContain(_server.Requests, r => r.Url.StartsWith(Thumb));
        Assert.Equal(1, _cache.GetStats().Changed);
    }

    [Fact]
    public async Task Removed_image_is_dropped()
    {
        await SeedAsync();
        _server.Serve(Source, [], status: HttpStatusCode.NotFound);
        _now += TimeSpan.FromHours(13);

        Assert.Equal(ImageOutcome.Gone, await _cache.EnsureAsync(Image));

        Assert.Null(_cache.GetThumbnailPath(Source));
        Assert.Equal(new[] { Source }, _changed);
    }

    [Fact]
    public async Task Error_page_or_outage_never_replaces_a_good_thumbnail()
    {
        await SeedAsync();
        _server.Serve(Source, Encoding.ASCII.GetBytes("<!DOCTYPE html><html>Access denied</html>"), etag: "\"blocked\"");
        _now += TimeSpan.FromHours(13);
        Assert.Equal(ImageOutcome.Failed, await _cache.EnsureAsync(Image));

        _server.Serve(Source, [], status: HttpStatusCode.ServiceUnavailable);
        Assert.Equal(ImageOutcome.Failed, await _cache.EnsureAsync(Image, force: true));

        Assert.Equal("-thumb-v1-padding", StoredContent());
        Assert.Empty(_changed);
    }

    [Fact]
    public async Task Server_ignoring_conditionals_but_reporting_the_same_version_is_unchanged()
    {
        await SeedAsync();
        _server.IgnoreConditionals = true;
        _now += TimeSpan.FromHours(13);

        Assert.Equal(ImageOutcome.Unchanged, await _cache.EnsureAsync(Image));
        Assert.Equal("-thumb-v1-padding", StoredContent());
        Assert.Equal(1, _cache.GetStats().Downloaded);
    }

    [Fact]
    public async Task Rotated_link_is_refetched_and_identical_pictures_share_one_file()
    {
        await SeedAsync();
        _cache.SyncReferences([Image]);

        const string rotated = "https://img.test/photos/a-v2.png";
        _server.Serve(rotated, Png("original-v1"), etag: "\"r1\"");
        var moved = Image with { SourceUrl = rotated, ThumbnailUrl = null };

        var work = _cache.SyncReferences([moved]);
        Assert.Equal(new[] { moved }, work);
        Assert.Equal(ImageOutcome.Fetched, await _cache.EnsureAsync(moved));
        Assert.Equal(1, _cache.GetStats().LinksChanged);

        // Same bytes as the old original → the new link reuses the existing file rather than duplicating it.
        _server.Serve(Source, Png("original-v1"), etag: "\"v1\"");
        await _cache.EnsureAsync(Image with { ThumbnailUrl = null }, force: true);
        var other = new ImageRef("store|p2|default", "https://img.test/photos/copy.png", null);
        _server.Serve(other.SourceUrl, Png("original-v1"), etag: "\"c1\"");
        await _cache.EnsureAsync(other);
        Assert.Equal(_cache.GetThumbnailPath(rotated), _cache.GetThumbnailPath(other.SourceUrl));
    }

    [Fact]
    public async Task Images_no_product_uses_for_a_week_are_deleted()
    {
        await SeedAsync();
        var path = _cache.GetThumbnailPath(Source)!;
        _cache.SyncReferences([Image]);

        _now += TimeSpan.FromDays(8);
        _cache.SyncReferences([]);

        Assert.Null(_cache.GetThumbnailPath(Source));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Failed_first_fetch_backs_off_before_retrying()
    {
        _server.Serve(Source, [], status: HttpStatusCode.ServiceUnavailable);
        var noThumb = Image with { ThumbnailUrl = null };
        Assert.Equal(ImageOutcome.Failed, await _cache.EnsureAsync(noThumb));
        Assert.Equal(ImageOutcome.Failed, await _cache.EnsureAsync(noThumb));
        Assert.Single(_server.Requests);

        _now += TimeSpan.FromMinutes(3);
        _server.Serve(Source, Png("original-v1"), etag: "\"v1\"");
        Assert.Equal(ImageOutcome.Fetched, await _cache.EnsureAsync(noThumb));
    }

    [Fact]
    public void Versioned_thumbnail_url_changes_with_the_original_version()
    {
        var a = ImageCache.VersionedThumbnailUrl("https://cdn.test/x.png?width=240", "\"v1\"", null);
        var b = ImageCache.VersionedThumbnailUrl("https://cdn.test/x.png?width=240", "\"v2\"", null);
        Assert.StartsWith("https://cdn.test/x.png?width=240&v=", a);
        Assert.NotEqual(a, b);
    }

    public void Dispose()
    {
        _cache.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeImageServer : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Body, string? ETag, HttpStatusCode Status)> _resources = new();
        public List<(string Method, string Url, string? IfNoneMatch, HttpStatusCode Status)> Log { get; } = new();
        public List<(string Method, string Url, string? IfNoneMatch, HttpStatusCode Status)> Requests => Log;
        public bool IgnoreConditionals { get; set; }

        public void Serve(string url, byte[] body, string? etag = null, HttpStatusCode status = HttpStatusCode.OK) =>
            _resources[url] = (body, etag, status);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var key = System.Text.RegularExpressions.Regex.Replace(url, @"[?&]v=[0-9a-f]+$", "");
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var v) ? v.First() : null;

            HttpResponseMessage response;
            if (!_resources.TryGetValue(key, out var r))
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            else if (!IgnoreConditionals && ifNoneMatch is not null && ifNoneMatch == r.ETag && r.Status == HttpStatusCode.OK)
                response = new HttpResponseMessage(HttpStatusCode.NotModified);
            else
            {
                response = new HttpResponseMessage(r.Status)
                {
                    Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : r.Body),
                };
                if (r.ETag is not null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(r.ETag);
            }
            lock (Log) Log.Add((request.Method.Method, url, ifNoneMatch, response.StatusCode));
            return Task.FromResult(response);
        }
    }
}

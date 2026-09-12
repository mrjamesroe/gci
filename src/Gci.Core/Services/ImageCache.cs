using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gci.Core.Services;

/// <summary>A product's image as the latest menu reports it.</summary>
/// <param name="ItemKey">The inventory item using the image.</param>
/// <param name="SourceUrl">The original image; its HTTP validators decide whether the picture changed.</param>
/// <param name="ThumbnailUrl">A smaller CDN variant used for the first download, when available.</param>
public sealed record ImageRef(string ItemKey, string SourceUrl, string? ThumbnailUrl);

public enum ImageStatus
{
    Ok,
    /// <summary>The provider removed the image (404/410); nothing is shown until it returns.</summary>
    Gone,
    /// <summary>Never fetched successfully; retried with backoff.</summary>
    Failed,
}

public enum ImageOutcome { Unchanged, Fetched, Changed, Gone, Failed }

public sealed record ImageEntry
{
    public string SourceUrl { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    /// <summary>Content hash naming the stored thumbnail file; identical pictures share one file.</summary>
    public string? File { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public ImageStatus Status { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset ValidatedAt { get; set; }
    public DateTimeOffset LastReferenced { get; set; }
    public int Failures { get; set; }
}

public sealed record ImageCacheStats(int Images, int Files, long Bytes, int Downloaded, int Revalidated, int Changed, int Removed, int LinksChanged);

/// <summary>
/// Disk cache of product thumbnails that keeps them in step with the providers:
/// <list type="bullet">
/// <item>Each thumbnail remembers its original image's ETag/Last-Modified. Once stale, the original is
/// re-checked with a conditional GET: 304 keeps the thumbnail, a new version rebuilds it from the original
/// itself (so a long-cached CDN resize can never serve an outdated picture), 404/410 drops it.</item>
/// <item>Which image each product uses is tracked, so a product whose image link rotates is refetched and the
/// old image expires. Thumbnails are content-addressed, so a re-hosted but identical picture is stored once.</item>
/// <item>Non-image responses (error pages) and undecodable files never replace a good thumbnail.</item>
/// </list>
/// </summary>
public sealed class ImageCache : IDisposable
{
    public static readonly TimeSpan DefaultRevalidateAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan ExpireUnreferencedAfter = TimeSpan.FromDays(7);
    private const int MaxConcurrentDownloads = 4;

    private readonly string _thumbDir;
    private readonly string _indexPath;
    private readonly HttpClient _http;
    private readonly Func<byte[], byte[]> _makeThumbnail;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _network = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly ConcurrentDictionary<string, Task<ImageOutcome>> _inFlight = new();
    private readonly object _lock = new();
    private readonly Timer _saveTimer;
    private CacheIndex _index;
    private int _downloaded, _revalidated, _changed, _removed, _linksChanged;

    /// <param name="root">Folder for the index and thumbnail files.</param>
    /// <param name="makeThumbnail">Turns original image bytes into stored thumbnail bytes; throws for undecodable input.</param>
    public ImageCache(string root, Func<byte[], byte[]> makeThumbnail, HttpMessageHandler? handler = null,
        string? userAgent = null, TimeSpan? revalidateAfter = null, Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(root);
        _thumbDir = Path.Combine(root, "thumbs");
        _indexPath = Path.Combine(root, "index.json");
        _makeThumbnail = makeThumbnail;
        _clock = clock ?? (() => DateTimeOffset.Now);
        RevalidateAfter = revalidateAfter ?? DefaultRevalidateAfter;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? "GCI/1.0");
        // No webp/avif in Accept: resizing CDNs then answer with JPEG/PNG, which every Windows build can decode.
        _http.DefaultRequestHeaders.Accept.ParseAdd("image/jpeg,image/png,image/gif;q=0.9,*/*;q=0.5");
        _index = LoadIndex();
        _saveTimer = new Timer(_ => SaveIndex(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan RevalidateAfter { get; }

    /// <summary>Raised (on a worker thread) with a source URL whose thumbnail appeared, changed, or was removed.</summary>
    public event Action<string>? ImageChanged;

    /// <summary>Path of the current thumbnail for an image, or null when there isn't one yet (or it was removed).</summary>
    public string? GetThumbnailPath(string sourceUrl)
    {
        lock (_lock)
        {
            if (!_index.Entries.TryGetValue(sourceUrl, out var e) || e.Status != ImageStatus.Ok || e.File is null) return null;
            var path = FilePath(e.File);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>True when the image has never been fetched or is due for a change check.</summary>
    public bool NeedsWork(string sourceUrl)
    {
        lock (_lock)
            return !_index.Entries.TryGetValue(sourceUrl, out var e) || IsDue(e, _clock());
    }

    /// <summary>
    /// Records the images the current menus use and returns the ones needing network work: cached images due
    /// for a change check, and new images for products that already had a cached picture under an older link.
    /// Also expires images no product has used for a week.
    /// </summary>
    public IReadOnlyList<ImageRef> SyncReferences(IEnumerable<ImageRef> refs)
    {
        var now = _clock();
        var work = new Dictionary<string, ImageRef>();
        lock (_lock)
        {
            var current = new HashSet<string>();
            foreach (var r in refs)
            {
                current.Add(r.ItemKey);
                if (_index.Items.TryGetValue(r.ItemKey, out var previous) && previous != r.SourceUrl)
                {
                    _linksChanged++;
                    if (_index.Entries.ContainsKey(previous) && !_index.Entries.ContainsKey(r.SourceUrl))
                        work[r.SourceUrl] = r;
                }
                _index.Items[r.ItemKey] = r.SourceUrl;

                if (_index.Entries.TryGetValue(r.SourceUrl, out var e))
                {
                    e.LastReferenced = now;
                    if (r.ThumbnailUrl is not null) e.ThumbnailUrl = r.ThumbnailUrl;
                    if (IsDue(e, now)) work[r.SourceUrl] = r;
                }
            }

            foreach (var (url, e) in _index.Entries.ToList())
                if (now - e.LastReferenced > ExpireUnreferencedAfter) _index.Entries.Remove(url);
            foreach (var (item, url) in _index.Items.ToList())
                if (!current.Contains(item) && !_index.Entries.ContainsKey(url)) _index.Items.Remove(item);
            DeleteOrphanFiles();
            ScheduleSave();
        }
        return work.Values.ToList();
    }

    /// <summary>
    /// Makes sure the image has a current thumbnail: fetches it when missing, and when stale (or <paramref name="force"/>)
    /// re-checks the original with a conditional request. Concurrent calls for one image share a single request.
    /// </summary>
    public Task<ImageOutcome> EnsureAsync(ImageRef image, bool force = false, CancellationToken ct = default)
    {
        return _inFlight.GetOrAdd(image.SourceUrl, url => Task.Run(async () =>
        {
            try
            {
                return await EnsureCoreAsync(image, force, ct);
            }
            finally
            {
                _inFlight.TryRemove(url, out _);
            }
        }, ct));
    }

    public ImageCacheStats GetStats()
    {
        lock (_lock)
        {
            var files = Directory.Exists(_thumbDir)
                ? Directory.EnumerateFiles(_thumbDir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList()
                : new List<FileInfo>();
            return new ImageCacheStats(
                _index.Entries.Values.Count(e => e.Status == ImageStatus.Ok),
                files.Count, files.Sum(f => f.Length),
                _downloaded, _revalidated, _changed, _removed, _linksChanged);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _index = new CacheIndex();
            if (Directory.Exists(_thumbDir)) Directory.Delete(_thumbDir, recursive: true);
            SaveIndexLocked();
        }
    }

    private async Task<ImageOutcome> EnsureCoreAsync(ImageRef image, bool force, CancellationToken ct)
    {
        ImageEntry? known;
        bool hasThumbnail;
        lock (_lock)
        {
            known = _index.Entries.TryGetValue(image.SourceUrl, out var e) ? e with { } : null;
            hasThumbnail = known?.File is not null && File.Exists(FilePath(known.File));
        }
        var now = _clock();
        if (!force && known is not null && !IsDue(known, now) && (hasThumbnail || known.Status != ImageStatus.Ok))
            return known.Status switch { ImageStatus.Gone => ImageOutcome.Gone, ImageStatus.Failed => ImageOutcome.Failed, _ => ImageOutcome.Unchanged };

        await _network.WaitAsync(ct);
        try
        {
            return hasThumbnail && known is not null
                ? await RevalidateAsync(image, known, ct)
                : await FetchAsync(image, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return RecordFailure(image);
        }
        finally
        {
            _network.Release();
        }
    }

    /// <summary>
    /// First download. The original's validators come first (HEAD), then the small CDN variant is requested with
    /// that version appended, so a resize the CDN cached from an older original can't be served. Falls back to
    /// downloading the original when there's no variant or it fails.
    /// </summary>
    private async Task<ImageOutcome> FetchAsync(ImageRef image, CancellationToken ct)
    {
        string? etag = null;
        DateTimeOffset? lastModified = null;
        byte[]? bytes = null;

        if (image.ThumbnailUrl is not null)
        {
            using (var head = new HttpRequestMessage(HttpMethod.Head, image.SourceUrl))
            using (var res = await _http.SendAsync(head, ct))
            {
                if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return RecordGone(image);
                if (res.IsSuccessStatusCode) (etag, lastModified) = Validators(res);
            }

            if (etag is not null || lastModified is not null)
            {
                using var res = await _http.GetAsync(VersionedThumbnailUrl(image.ThumbnailUrl, etag, lastModified), ct);
                if (res.IsSuccessStatusCode)
                {
                    bytes = await res.Content.ReadAsByteArrayAsync(ct);
                    if (!LooksLikeImage(bytes)) bytes = null;
                }
            }
        }

        if (bytes is null)
        {
            using var res = await _http.GetAsync(image.SourceUrl, ct);
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return RecordGone(image);
            if (!res.IsSuccessStatusCode) return RecordFailure(image);
            (etag, lastModified) = Validators(res);
            bytes = await res.Content.ReadAsByteArrayAsync(ct);
        }

        return Store(image, bytes, etag, lastModified);
    }

    /// <summary>Adds the original's version to the CDN URL; resizing CDNs key their cache on the full URL.</summary>
    internal static string VersionedThumbnailUrl(string thumbnailUrl, string? etag, DateTimeOffset? lastModified)
    {
        var version = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{etag}|{lastModified?.ToUnixTimeSeconds()}")))[..12].ToLowerInvariant();
        return $"{thumbnailUrl}{(thumbnailUrl.Contains('?') ? '&' : '?')}v={version}";
    }

    /// <summary>Conditional GET on the original. Only a new version is downloaded, and it's rebuilt from the original.</summary>
    private async Task<ImageOutcome> RevalidateAsync(ImageRef image, ImageEntry known, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, image.SourceUrl);
        if (known.ETag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", known.ETag);
        if (known.LastModified is { } lm) req.Headers.IfModifiedSince = lm;

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotModified)
            return RecordUnchanged(image, known.ETag, known.LastModified);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return RecordGone(image);
        if (!res.IsSuccessStatusCode)
            return RecordFailure(image);

        var (etag, lastModified) = Validators(res);
        var sameVersion = etag is not null && known.ETag is not null
            ? etag == known.ETag
            : lastModified is not null && known.LastModified is not null && lastModified == known.LastModified;
        if (sameVersion)
            return RecordUnchanged(image, etag, lastModified); // server ignored the conditional headers

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return Store(image, bytes, etag, lastModified);
    }

    private ImageOutcome Store(ImageRef image, byte[] original, string? etag, DateTimeOffset? lastModified)
    {
        if (!LooksLikeImage(original)) return RecordFailure(image);
        byte[] thumbnail;
        try
        {
            thumbnail = _makeThumbnail(original);
        }
        catch (Exception)
        {
            return RecordFailure(image);
        }

        var hash = Convert.ToHexString(SHA256.HashData(thumbnail))[..32].ToLowerInvariant();
        var now = _clock();
        bool changed;
        lock (_lock)
        {
            var path = FilePath(hash);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, thumbnail);
            }

            var e = GetOrCreate(image, now);
            var previousFile = e.Status == ImageStatus.Ok ? e.File : null;
            changed = previousFile is not null && previousFile != hash;
            e.File = hash;
            e.ETag = etag;
            e.LastModified = lastModified;
            e.Status = ImageStatus.Ok;
            e.Failures = 0;
            e.FetchedAt = now;
            e.ValidatedAt = now;
            _downloaded++;
            if (changed) _changed++;
            if (previousFile == hash) _revalidated++;
            ScheduleSave();
            if (previousFile == hash) return ImageOutcome.Unchanged;
        }

        ImageChanged?.Invoke(image.SourceUrl);
        return changed ? ImageOutcome.Changed : ImageOutcome.Fetched;
    }

    private ImageOutcome RecordUnchanged(ImageRef image, string? etag, DateTimeOffset? lastModified)
    {
        lock (_lock)
        {
            var e = GetOrCreate(image, _clock());
            e.ValidatedAt = _clock();
            e.ETag = etag ?? e.ETag;
            e.LastModified = lastModified ?? e.LastModified;
            e.Failures = 0;
            _revalidated++;
            ScheduleSave();
        }
        return ImageOutcome.Unchanged;
    }

    private ImageOutcome RecordGone(ImageRef image)
    {
        bool hadThumbnail;
        lock (_lock)
        {
            var e = GetOrCreate(image, _clock());
            hadThumbnail = e.Status == ImageStatus.Ok && e.File is not null;
            e.Status = ImageStatus.Gone;
            e.File = null;
            e.ValidatedAt = _clock();
            if (hadThumbnail) _removed++;
            ScheduleSave();
        }
        if (hadThumbnail) ImageChanged?.Invoke(image.SourceUrl);
        return ImageOutcome.Gone;
    }

    /// <summary>Keeps any existing thumbnail (a failed check isn't evidence the picture changed) and backs off.</summary>
    private ImageOutcome RecordFailure(ImageRef image)
    {
        lock (_lock)
        {
            var e = GetOrCreate(image, _clock());
            e.Failures++;
            e.ValidatedAt = _clock();
            if (e.File is null) e.Status = ImageStatus.Failed;
            ScheduleSave();
        }
        return ImageOutcome.Failed;
    }

    private bool IsDue(ImageEntry e, DateTimeOffset now)
    {
        var wait = e.Status == ImageStatus.Failed
            ? TimeSpan.FromMinutes(Math.Min(RevalidateAfter.TotalMinutes, Math.Pow(2, Math.Min(e.Failures, 10))))
            : RevalidateAfter;
        return now - e.ValidatedAt >= wait;
    }

    private ImageEntry GetOrCreate(ImageRef image, DateTimeOffset now)
    {
        if (!_index.Entries.TryGetValue(image.SourceUrl, out var e))
        {
            e = new ImageEntry { SourceUrl = image.SourceUrl, Status = ImageStatus.Failed, LastReferenced = now };
            _index.Entries[image.SourceUrl] = e;
        }
        e.ThumbnailUrl = image.ThumbnailUrl ?? e.ThumbnailUrl;
        e.LastReferenced = now;
        return e;
    }

    private void DeleteOrphanFiles()
    {
        if (!Directory.Exists(_thumbDir)) return;
        var live = _index.Entries.Values.Select(e => e.File).OfType<string>().ToHashSet();
        foreach (var file in Directory.EnumerateFiles(_thumbDir, "*.jpg", SearchOption.AllDirectories))
            if (!live.Contains(Path.GetFileNameWithoutExtension(file)))
                File.Delete(file);
    }

    private string FilePath(string hash) => Path.Combine(_thumbDir, hash[..2], hash + ".jpg");

    private static (string? ETag, DateTimeOffset? LastModified) Validators(HttpResponseMessage res) =>
        (res.Headers.ETag?.ToString(), res.Content.Headers.LastModified);

    /// <summary>Magic-number check so an HTML error page served with 200 is never treated as a picture.</summary>
    internal static bool LooksLikeImage(byte[] b) =>
        b.Length > 12 && (
            (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||                               // JPEG
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||              // PNG
            (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) ||                               // GIF
            (b[0] == 0x42 && b[1] == 0x4D) ||                                               // BMP
            (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
             b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) ||            // WebP
            (b[4] == 0x66 && b[5] == 0x74 && b[6] == 0x79 && b[7] == 0x70));               // AVIF/HEIC (ftyp)

    private CacheIndex LoadIndex()
    {
        try
        {
            if (File.Exists(_indexPath))
                return JsonSerializer.Deserialize<CacheIndex>(File.ReadAllText(_indexPath), DataStore.Options) ?? new();
        }
        catch (JsonException)
        {
            // Start over; the thumbnails are just a cache.
        }
        return new CacheIndex();
    }

    private void ScheduleSave() => _saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

    private void SaveIndex()
    {
        lock (_lock) SaveIndexLocked();
    }

    private void SaveIndexLocked()
    {
        var temp = _indexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_index, DataStore.Options));
        File.Move(temp, _indexPath, overwrite: true);
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveIndex();
        _http.Dispose();
        _network.Dispose();
    }

    internal sealed class CacheIndex
    {
        public Dictionary<string, ImageEntry> Entries { get; set; } = new();
        /// <summary>Item key → the source URL it used last time, to notice rotated image links.</summary>
        public Dictionary<string, string> Items { get; set; } = new();
    }
}

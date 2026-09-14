using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Gci.App.Services;
using Gci.Core.Services;

namespace Gci.Desktop.Services;

/// <summary>
/// The Avalonia counterpart of the WPF ThumbnailService: wraps Core's <see cref="ImageCache"/>, decodes cached
/// thumbnails into Avalonia <see cref="Bitmap"/>s (with a small in-memory LRU), and raises changes on the UI thread
/// so visible rows swap to a new picture. Implements <see cref="IThumbnailCache"/> for the shared ViewModel.
/// </summary>
public sealed class DesktopThumbnailService : IThumbnailCache, IDisposable
{
    private const int MemoryCapacity = 800;

    private readonly ImageCache _cache;
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Image)>> _memory = new();
    private readonly LinkedList<(string Key, Bitmap Image)> _order = new();

    public DesktopThumbnailService(DataStore data, string userAgent)
    {
        _cache = new ImageCache(data.PathFor("images"), DesktopThumbnailRenderer.Render, userAgent: userAgent);
        _cache.ImageChanged += url => Dispatcher.UIThread.Post(() => ImageChanged?.Invoke(url));
    }

    /// <summary>Raised on the UI thread with the source URL of an image whose thumbnail appeared, changed, or was removed.</summary>
    public event Action<string>? ImageChanged;

    /// <summary>A thumbnail already decoded at this size, without touching disk or network.</summary>
    public Bitmap? TryGetDecoded(ImageRef image, int size)
    {
        var path = _cache.GetThumbnailPath(image.SourceUrl);
        return path is not null && _memory.TryGetValue(Key(path, size), out var node) ? Touch(node) : null;
    }

    /// <summary>The current thumbnail: a cached one right away (re-checked in the background if due), else downloaded first.</summary>
    public async Task<Bitmap?> GetAsync(ImageRef image, int size)
    {
        var path = _cache.GetThumbnailPath(image.SourceUrl);
        if (path is null)
        {
            await _cache.EnsureAsync(image);
            path = _cache.GetThumbnailPath(image.SourceUrl);
            if (path is null) return null;
        }
        else if (_cache.NeedsWork(image.SourceUrl))
        {
            _ = _cache.EnsureAsync(image); // a change raises ImageChanged and the row reloads
        }

        var key = Key(path, size);
        if (_memory.TryGetValue(key, out var node)) return Touch(node);
        var bitmap = await Task.Run(() => Decode(path, size));
        if (bitmap is null) return null;
        Remember(key, bitmap);
        return bitmap;
    }

    public Task SweepAsync(IReadOnlyCollection<ImageRef> images, bool force = false)
    {
        var work = _cache.SyncReferences(images);
        if (force)
            work = images.Where(i => _cache.GetThumbnailPath(i.SourceUrl) is not null).DistinctBy(i => i.SourceUrl).ToList();
        return Task.WhenAll(work.Select(i => _cache.EnsureAsync(i, force)));
    }

    public ImageCacheStats GetStats() => _cache.GetStats();

    public void Clear()
    {
        _memory.Clear();
        _order.Clear();
        _cache.Clear();
    }

    private static Bitmap? Decode(string path, int size)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            return null; // deleted mid-read or unreadable; the next request refetches
        }
    }

    private static string Key(string path, int size) => $"{path}|{size}";

    private Bitmap Touch(LinkedListNode<(string Key, Bitmap Image)> node)
    {
        _order.Remove(node);
        _order.AddFirst(node);
        return node.Value.Image;
    }

    private void Remember(string key, Bitmap bitmap)
    {
        if (_memory.ContainsKey(key)) return;
        _memory[key] = _order.AddFirst((key, bitmap));
        // Evict least-recently-used, but don't Dispose — an evicted bitmap may still be a live Image.Source.
        while (_memory.Count > MemoryCapacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _memory.Remove(last.Value.Key);
        }
    }

    public void Dispose() => _cache.Dispose();
}

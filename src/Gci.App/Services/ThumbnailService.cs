using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gci.Core.Services;

namespace Gci.App.Services;

/// <summary>
/// UI-facing wrapper over <see cref="ImageCache"/>: decodes cached thumbnails into frozen bitmaps (with a small
/// in-memory LRU), shows cached pictures immediately while stale ones are re-checked in the background, and
/// announces changes on the UI thread so visible rows swap to the new picture.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int MemoryCapacity = 800;

    private readonly ImageCache _cache;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> _memory = new();
    private readonly LinkedList<(string Key, BitmapSource Image)> _order = new();

    public ThumbnailService(DataStore data, string userAgent, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _cache = new ImageCache(data.PathFor("images"), ThumbnailRenderer.Render, userAgent: userAgent);
        _cache.ImageChanged += url => _dispatcher.BeginInvoke(() => ImageChanged?.Invoke(url));
    }

    /// <summary>Raised on the UI thread with the source URL of an image whose thumbnail appeared, changed, or was removed.</summary>
    public event Action<string>? ImageChanged;

    /// <summary>A thumbnail already decoded at this size, without touching disk or network.</summary>
    public BitmapSource? TryGetDecoded(ImageRef image, int size)
    {
        var path = _cache.GetThumbnailPath(image.SourceUrl);
        return path is not null && _memory.TryGetValue(Key(path, size), out var node) ? Touch(node) : null;
    }

    /// <summary>
    /// The current thumbnail. A cached one is returned right away (and re-checked in the background if due);
    /// a missing one is downloaded first.
    /// </summary>
    public async Task<BitmapSource?> GetAsync(ImageRef image, int size)
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

    /// <summary>After a menu refresh: records which images products use and re-checks the due ones in the background.</summary>
    public Task SweepAsync(IReadOnlyCollection<ImageRef> images, bool force = false)
    {
        var work = _cache.SyncReferences(images);
        if (force)
            work = images.Where(i => _cache.GetThumbnailPath(i.SourceUrl) is not null).DistinctBy(i => i.SourceUrl).ToList();
        return Task.WhenAll(work.Select(i => _cache.EnsureAsync(i, force)));
    }

    public ImageCacheStats GetStats() => _cache.GetStats();

    public TimeSpan RevalidateAfter => _cache.RevalidateAfter;

    public void Clear()
    {
        _memory.Clear();
        _order.Clear();
        _cache.Clear();
    }

    private static BitmapSource? Decode(string path, int size)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            bitmap.DecodePixelWidth = size;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null; // deleted mid-read or unreadable; the next request refetches
        }
    }

    private static string Key(string path, int size) => $"{path}|{size}";

    private BitmapSource Touch(LinkedListNode<(string Key, BitmapSource Image)> node)
    {
        _order.Remove(node);
        _order.AddFirst(node);
        return node.Value.Image;
    }

    private void Remember(string key, BitmapSource bitmap)
    {
        if (_memory.ContainsKey(key)) return;
        _memory[key] = _order.AddFirst((key, bitmap));
        while (_memory.Count > MemoryCapacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _memory.Remove(last.Value.Key);
        }
    }

    public void Dispose() => _cache.Dispose();
}

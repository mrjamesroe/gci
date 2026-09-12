using System.Windows;
using System.Windows.Controls;
using Gci.App.Services;
using Gci.Core.Services;

namespace Gci.App.Views;

/// <summary>
/// Attached behavior for product images: <c>&lt;Image views:Thumb.Image="{Binding Image}" views:Thumb.Size="80" /&gt;</c>.
/// Loads asynchronously, ignores results for rows that were recycled in the meantime, and reloads visible images
/// when the cache reports the picture changed.
/// </summary>
public static class Thumb
{
    private static ThumbnailService? _service;
    private static readonly Dictionary<string, List<WeakReference<Image>>> Visible = new();

    public static ThumbnailService? Service
    {
        get => _service;
        set
        {
            if (_service is not null) _service.ImageChanged -= Reload;
            _service = value;
            if (_service is not null) _service.ImageChanged += Reload;
        }
    }

    public static readonly DependencyProperty ImageProperty = DependencyProperty.RegisterAttached(
        "Image", typeof(ImageRef), typeof(Thumb), new PropertyMetadata(null, (d, args) => { if (d is Image i) _ = LoadAsync(i); }));

    /// <summary>Decode width in pixels (2× the display size keeps it crisp on high-DPI screens).</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(int), typeof(Thumb), new PropertyMetadata(96));

    public static ImageRef? GetImage(DependencyObject d) => (ImageRef?)d.GetValue(ImageProperty);
    public static void SetImage(DependencyObject d, ImageRef? value) => d.SetValue(ImageProperty, value);
    public static int GetSize(DependencyObject d) => (int)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, int value) => d.SetValue(SizeProperty, value);

    private static async Task LoadAsync(Image target)
    {
        var image = GetImage(target);
        var size = GetSize(target);
        if (image is null || Service is null)
        {
            target.Source = null;
            return;
        }

        Track(image.SourceUrl, target);
        if (Service.TryGetDecoded(image, size) is { } ready)
        {
            target.Source = ready;
            return;
        }

        target.Source = null;
        var bitmap = await Service.GetAsync(image, size);
        if (Equals(GetImage(target), image)) target.Source = bitmap;
    }

    private static void Track(string url, Image target)
    {
        if (!Visible.TryGetValue(url, out var list)) Visible[url] = list = new();
        list.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, target));
        list.Add(new WeakReference<Image>(target));
    }

    private static void Reload(string url)
    {
        if (!Visible.TryGetValue(url, out var list)) return;
        foreach (var weak in list.ToList())
        {
            if (weak.TryGetTarget(out var target) && GetImage(target)?.SourceUrl == url) _ = LoadAsync(target);
            else list.Remove(weak);
        }
        if (list.Count == 0) Visible.Remove(url);
    }
}

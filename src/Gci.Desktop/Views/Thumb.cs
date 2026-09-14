using Avalonia;
using Avalonia.Controls;
using Gci.Core.Services;
using Gci.Desktop.Services;

namespace Gci.Desktop.Views;

/// <summary>
/// Attached behavior for product images: <c>&lt;Image views:Thumb.Image="{Binding Image}" views:Thumb.Size="84" /&gt;</c>.
/// Loads asynchronously, ignores results for rows recycled in the meantime, and reloads visible images when the cache
/// reports the picture changed. The Avalonia counterpart of the WPF Thumb behavior.
/// </summary>
// Not static: an attached property's owner type is used as a generic type argument, which a static class can't be.
public sealed class Thumb
{
    private Thumb() { }

    private static DesktopThumbnailService? _service;
    private static readonly Dictionary<string, List<WeakReference<Image>>> Visible = new();

    static Thumb()
    {
        ImageProperty.Changed.AddClassHandler<Image>((img, e) => { _ = LoadAsync(img); });
    }

    public static DesktopThumbnailService? Service
    {
        get => _service;
        set
        {
            if (_service is not null) _service.ImageChanged -= Reload;
            _service = value;
            if (_service is not null) _service.ImageChanged += Reload;
        }
    }

    public static readonly AttachedProperty<ImageRef?> ImageProperty =
        AvaloniaProperty.RegisterAttached<Thumb, Image, ImageRef?>("Image");

    /// <summary>Decode width in pixels (2× the display size keeps it crisp on high-DPI screens).</summary>
    public static readonly AttachedProperty<int> SizeProperty =
        AvaloniaProperty.RegisterAttached<Thumb, Image, int>("Size", 96);

    public static ImageRef? GetImage(Image target) => target.GetValue(ImageProperty);
    public static void SetImage(Image target, ImageRef? value) => target.SetValue(ImageProperty, value);
    public static int GetSize(Image target) => target.GetValue(SizeProperty);
    public static void SetSize(Image target, int value) => target.SetValue(SizeProperty, value);

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

using SkiaSharp;

namespace Gci.Desktop.Services;

/// <summary>
/// Turns a provider's original image into a small JPEG: scaled to fit <see cref="MaxSize"/> and flattened onto white
/// (so transparent PNG backgrounds don't turn black in JPEG). The SkiaSharp counterpart of the WPF ThumbnailRenderer;
/// runs off the UI thread. (EXIF auto-rotation, which the WPF renderer does, isn't applied here — product-menu images
/// are already upright; can revisit if a sideways one shows up.)
/// </summary>
public static class DesktopThumbnailRenderer
{
    public const int MaxSize = 320;

    public static byte[] Render(byte[] source)
    {
        using var bitmap = SKBitmap.Decode(source)
            ?? throw new InvalidOperationException("Undecodable image."); // ImageCache treats a throw as "not an image"

        var scale = Math.Min(1.0, (double)MaxSize / Math.Max(bitmap.Width, bitmap.Height));
        var w = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var h = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White); // opaque white ground; source-over draw flattens any alpha onto it
        using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
            canvas.DrawBitmap(bitmap, new SKRect(0, 0, bitmap.Width, bitmap.Height), new SKRect(0, 0, w, h), paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}

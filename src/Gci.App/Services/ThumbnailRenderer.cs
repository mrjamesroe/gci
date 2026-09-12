using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Gci.App.Services;

/// <summary>
/// Turns a provider's original image into a small JPEG: scaled to fit <see cref="MaxSize"/>, rotated upright per
/// its EXIF orientation (so a phone photo tagged "rotate 90°" isn't shown sideways), and flattened onto white
/// (transparent PNG backgrounds would otherwise turn black in JPEG). Safe to call off the UI thread.
/// </summary>
public static class ThumbnailRenderer
{
    public const int MaxSize = 320;

    public static byte[] Render(byte[] source)
    {
        using var input = new MemoryStream(source);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        var scale = Math.Min(1.0, (double)MaxSize / Math.Max(frame.PixelWidth, frame.PixelHeight));
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale));
        foreach (var t in OrientationTransforms(ReadOrientation(frame)))
            transform.Children.Add(t);
        BitmapSource shaped = transform.Value.IsIdentity ? frame : new TransformedBitmap(frame, transform);

        var bgra = new FormatConvertedBitmap(shaped, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);

        var rgb = new byte[w * h * 3];
        for (int i = 0, o = 0; i < pixels.Length; i += 4, o += 3)
        {
            var a = pixels[i + 3];
            rgb[o] = Blend(pixels[i], a);
            rgb[o + 1] = Blend(pixels[i + 1], a);
            rgb[o + 2] = Blend(pixels[i + 2], a);
        }

        var flattened = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>Straight (non-premultiplied) alpha over a white background.</summary>
    private static byte Blend(byte channel, byte alpha) => (byte)((channel * alpha + 255 * (255 - alpha) + 127) / 255);

    internal static ushort ReadOrientation(BitmapSource frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o and >= 1 and <= 8)
                return o;
        }
        catch (Exception)
        {
            // Formats without EXIF (PNG, GIF) throw or return nothing.
        }
        return 1;
    }

    /// <summary>EXIF orientation 1–8 → the rotation/flip that makes the image upright.</summary>
    internal static IEnumerable<Transform> OrientationTransforms(ushort orientation) => orientation switch
    {
        2 => [new ScaleTransform(-1, 1)],
        3 => [new RotateTransform(180)],
        4 => [new ScaleTransform(1, -1)],
        5 => [new RotateTransform(90), new ScaleTransform(-1, 1)],
        6 => [new RotateTransform(90)],
        7 => [new RotateTransform(270), new ScaleTransform(-1, 1)],
        8 => [new RotateTransform(270)],
        _ => [],
    };
}

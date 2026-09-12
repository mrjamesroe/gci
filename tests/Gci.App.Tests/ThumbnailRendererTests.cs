using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gci.App.Services;

namespace Gci.App.Tests;

public class ThumbnailRendererTests
{
    /// <summary>A w×h image whose left half is red and right half blue, optionally with transparency.</summary>
    private static BitmapSource Split(int w, int h, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var left = x < w / 2;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = left ? ((byte)0, (byte)0, (byte)255, alpha) : ((byte)255, (byte)0, (byte)0, alpha);
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource image, BitmapMetadata? metadata = null)
    {
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource Decode(byte[] jpeg) =>
        BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

    private static (byte R, byte G, byte B) Pixel(BitmapSource image, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    [Fact]
    public void Scales_to_fit_and_keeps_aspect_ratio()
    {
        var thumb = Decode(ThumbnailRenderer.Render(Encode(new PngBitmapEncoder(), Split(1600, 800))));
        Assert.Equal((ThumbnailRenderer.MaxSize, ThumbnailRenderer.MaxSize / 2), (thumb.PixelWidth, thumb.PixelHeight));
    }

    [Fact]
    public void Small_images_are_not_upscaled()
    {
        var thumb = Decode(ThumbnailRenderer.Render(Encode(new PngBitmapEncoder(), Split(120, 90))));
        Assert.Equal((120, 90), (thumb.PixelWidth, thumb.PixelHeight));
    }

    [Fact]
    public void Exif_rotation_is_applied_so_the_picture_is_upright()
    {
        // Stored landscape with orientation 6 ("rotate 90° clockwise to view"): the left (red) half should end up on top.
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var jpeg = Encode(new JpegBitmapEncoder { QualityLevel = 95 }, Split(400, 200), metadata);

        var thumb = Decode(ThumbnailRenderer.Render(jpeg));

        Assert.Equal((160, 320), (thumb.PixelWidth, thumb.PixelHeight));
        var top = Pixel(thumb, 80, 40);
        var bottom = Pixel(thumb, 80, 280);
        Assert.True(top.R > 200 && top.B < 60, $"top should be red, was {top}");
        Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom should be blue, was {bottom}");
    }

    [Fact]
    public void Transparency_is_flattened_onto_white()
    {
        var thumb = Decode(ThumbnailRenderer.Render(Encode(new PngBitmapEncoder(), Split(100, 100, alpha: 0))));
        var p = Pixel(thumb, 25, 50);
        Assert.True(p.R > 245 && p.G > 245 && p.B > 245, $"expected white, was {p}");
    }

    [Fact]
    public void Undecodable_bytes_throw_so_the_cache_keeps_the_old_thumbnail()
    {
        Assert.ThrowsAny<Exception>(() => ThumbnailRenderer.Render("<!DOCTYPE html><html>nope</html>"u8.ToArray()));
    }
}

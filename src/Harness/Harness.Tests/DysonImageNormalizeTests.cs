using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// ponytail: Magick ICO/BMP → PNG normalize + alpha preserved.
/// </summary>
public class DysonImageNormalizeTests
{
    [Fact]
    public void Run()
    {
        AssertAllowlist();
        AssertIcoToPngWithAlpha();
        AssertBmpToPngShrinks();
    }

    private static void AssertAllowlist()
    {
        if (!DysonImageNormalize.IsProviderNativeImageMime("image/png")
            || !DysonImageNormalize.IsProviderNativeImageMime("image/jpeg")
            || !DysonImageNormalize.IsProviderNativeImageMime("IMAGE/GIF")
            || !DysonImageNormalize.IsProviderNativeImageMime("image/webp"))
        {
            throw new InvalidOperationException("png/jpeg/gif/webp must be provider-native.");
        }

        if (DysonImageNormalize.IsProviderNativeImageMime("image/x-icon")
            || DysonImageNormalize.IsProviderNativeImageMime("image/bmp")
            || DysonImageNormalize.IsProviderNativeImageMime("image/tiff")
            || DysonImageNormalize.IsProviderNativeImageMime("image/svg+xml")
            || DysonImageNormalize.IsProviderNativeImageMime("application/octet-stream"))
        {
            throw new InvalidOperationException("Non-allowlisted MIME must not be provider-native.");
        }
    }

    private static void AssertIcoToPngWithAlpha()
    {
        byte[] ico;
        using (var image = new MagickImage(MagickColors.Transparent, 32, 32))
        {
            image.Format = MagickFormat.Ico;
            ico = image.ToByteArray();
        }

        var png = DysonImageNormalize.ToPngMaxEdge(ico, readFormat: MagickFormat.Ico);
        if (png.MimeType != "image/png")
            throw new InvalidOperationException("ToPngMaxEdge must emit image/png.");
        if (png.Bytes.Length < 8
            || png.Bytes[0] != 0x89
            || png.Bytes[1] != 0x50
            || png.Bytes[2] != 0x4E
            || png.Bytes[3] != 0x47)
        {
            throw new InvalidOperationException("ToPngMaxEdge must emit PNG magic bytes.");
        }

        using var check = new MagickImage(png.Bytes);
        if (!check.HasAlpha)
            throw new InvalidOperationException("PNG from transparent ICO must keep alpha.");
        if (check.Width != 32 || check.Height != 32)
            throw new InvalidOperationException("Small ICO must not be upscaled.");
    }

    private static void AssertBmpToPngShrinks()
    {
        byte[] bmp;
        using (var image = new MagickImage(MagickColors.Red, 2000, 1000))
        {
            image.Format = MagickFormat.Bmp;
            bmp = image.ToByteArray();
        }

        var png = DysonImageNormalize.ToPngMaxEdge(bmp, readFormat: MagickFormat.Bmp);
        if (png.MimeType != "image/png"
            || png.Width > DysonImageCompress.DefaultMaxEdge
            || png.Height > DysonImageCompress.DefaultMaxEdge)
        {
            throw new InvalidOperationException(
                $"BMP normalize must be PNG with edge ≤ {DysonImageCompress.DefaultMaxEdge}.");
        }

        if (png.Width != 1280 || png.Height != 640)
        {
            throw new InvalidOperationException(
                $"Expected 1280x640 after shrink, got {png.Width}x{png.Height}.");
        }
    }
}

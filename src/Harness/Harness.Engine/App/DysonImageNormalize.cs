using ImageMagick;

namespace DysonHarness;

/// <summary>
/// Magick.NET helpers that turn provider-unsafe image MIME types into PNG
/// (alpha preserved) before multimodal attachment.
/// </summary>
public static class DysonImageNormalize
{
    /// <summary>
    /// OpenAI-common vision MIME types that LoadBinary may attach without conversion.
    /// </summary>
    public static bool IsProviderNativeImageMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        return mimeType.Trim().ToLowerInvariant() switch
        {
            "image/png" or "image/jpeg" or "image/gif" or "image/webp" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Maps known non-native image MIME types to a Magick read format hint
    /// (blob sniffing alone fails for ICO and some others).
    /// </summary>
    public static MagickFormat? TryMagickFormatFromImageMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return null;

        return mimeType.Trim().ToLowerInvariant() switch
        {
            "image/x-icon" or "image/vnd.microsoft.icon" => MagickFormat.Ico,
            "image/bmp" or "image/x-ms-bmp" => MagickFormat.Bmp,
            "image/tiff" => MagickFormat.Tiff,
            "image/svg+xml" => MagickFormat.Svg,
            _ => null,
        };
    }

    /// <summary>
    /// Decodes <paramref name="imageBytes"/>, shrinks longest edge to at most
    /// <paramref name="maxEdge"/> (no upscale), writes PNG with alpha preserved.
    /// Pass <paramref name="readFormat"/> when blob sniffing is unreliable (e.g. ICO).
    /// </summary>
    public static DysonImageCompress.CompressedImage ToPngMaxEdge(
        byte[] imageBytes,
        int maxEdge = DysonImageCompress.DefaultMaxEdge,
        MagickFormat? readFormat = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image bytes are empty.", nameof(imageBytes));
        if (maxEdge < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEdge));

        using MagickImage image = readFormat is { } format
            ? new MagickImage(imageBytes, new MagickReadSettings { Format = format })
            : new MagickImage(imageBytes);

        if (image.Width > (uint)maxEdge || image.Height > (uint)maxEdge)
        {
            // ImageMagick `>` geometry: shrink only when larger than the box.
            var geometry = new MagickGeometry((uint)maxEdge, (uint)maxEdge)
            {
                IgnoreAspectRatio = false,
                Greater = true,
            };
            image.Resize(geometry);
        }

        image.Format = MagickFormat.Png;
        var png = image.ToByteArray();

        return new DysonImageCompress.CompressedImage(
            png,
            (int)image.Width,
            (int)image.Height,
            "image/png");
    }
}

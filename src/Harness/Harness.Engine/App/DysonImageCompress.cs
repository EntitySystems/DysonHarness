using ImageMagick;

namespace DysonHarness;

/// <summary>
/// Magick.NET helpers for shrinking screenshots before multimodal attachment.
/// </summary>
public static class DysonImageCompress
{
    public const int DefaultMaxEdge = 1280;
    public const uint DefaultJpegQuality = 75;

    public readonly record struct CompressedImage(
        byte[] Bytes,
        int Width,
        int Height,
        string MimeType);

    /// <summary>
    /// Decodes <paramref name="imageBytes"/>, shrinks longest edge to at most
    /// <paramref name="maxEdge"/> (no upscale), writes JPEG at <paramref name="quality"/>.
    /// </summary>
    public static CompressedImage ToJpegMaxEdge(
        byte[] imageBytes,
        int maxEdge = DefaultMaxEdge,
        uint quality = DefaultJpegQuality)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image bytes are empty.", nameof(imageBytes));
        if (maxEdge < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEdge));

        using var image = new MagickImage(imageBytes);

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

        image.Quality = quality;
        image.Format = MagickFormat.Jpeg;
        var jpeg = image.ToByteArray();

        return new CompressedImage(
            jpeg,
            (int)image.Width,
            (int)image.Height,
            "image/jpeg");
    }
}

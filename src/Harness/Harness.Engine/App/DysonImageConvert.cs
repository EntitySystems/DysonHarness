using ImageMagick;

namespace DysonHarness;

/// <summary>
/// Magick.NET helpers for workspace image conversion (ConvertImage MCP).
/// Separate from <see cref="DysonImageNormalize"/> (LoadBinary vision PNG path).
/// </summary>
public static class DysonImageConvert
{
    public const int DefaultQuality = 85;

    public readonly record struct ConvertedImage(
        byte[] Bytes,
        int Width,
        int Height,
        string DesiredFormat,
        int Quality);

    /// <summary>
    /// Maps a ConvertImage <c>desiredFormat</c> id to Magick write format.
    /// </summary>
    public static Result<MagickFormat, string> TryParseDesiredFormat(string? desiredFormat)
    {
        if (string.IsNullOrWhiteSpace(desiredFormat))
            return Result<MagickFormat, string>.AsError("desiredFormat is required.");

        return desiredFormat.Trim().ToLowerInvariant() switch
        {
            "png" => Result<MagickFormat, string>.AsValue(MagickFormat.Png),
            "jpeg" or "jpg" => Result<MagickFormat, string>.AsValue(MagickFormat.Jpeg),
            "webp" => Result<MagickFormat, string>.AsValue(MagickFormat.WebP),
            "gif" => Result<MagickFormat, string>.AsValue(MagickFormat.Gif),
            "bmp" => Result<MagickFormat, string>.AsValue(MagickFormat.Bmp),
            "tiff" or "tif" => Result<MagickFormat, string>.AsValue(MagickFormat.Tiff),
            "ico" => Result<MagickFormat, string>.AsValue(MagickFormat.Ico),
            _ => Result<MagickFormat, string>.AsError(
                $"Unsupported desiredFormat '{desiredFormat}'. " +
                "Use png, jpeg/jpg, webp, gif, bmp, tiff/tif, or ico."),
        };
    }

    /// <summary>
    /// Canonical lowercase format id for ack JSON (jpg → jpeg, tif → tiff).
    /// </summary>
    public static string CanonicalFormatId(string desiredFormat) =>
        desiredFormat.Trim().ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            "tif" => "tiff",
            var id => id,
        };

    /// <summary>
    /// Magick read-format hint from a file extension (SVG/ICO and other sniff-unreliable types).
    /// </summary>
    public static MagickFormat? TryMagickFormatFromExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var mime = extension.Trim().ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".svg" => "image/svg+xml",
            _ => null,
        };

        return mime is null
            ? null
            : DysonImageNormalize.TryMagickFormatFromImageMime(mime);
    }

    /// <summary>
    /// Decodes <paramref name="imageBytes"/> (optional read hint), re-encodes to
    /// <paramref name="desiredFormat"/> at <paramref name="quality"/> (1–100).
    /// Same-format is a normal Magick re-encode. ICO is single-frame.
    /// </summary>
    public static Result<ConvertedImage, string> Convert(
        byte[] imageBytes,
        string desiredFormat,
        int quality = DefaultQuality,
        MagickFormat? readFormat = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            return Result<ConvertedImage, string>.AsError("Image bytes are empty.");

        if (quality is < 1 or > 100)
        {
            return Result<ConvertedImage, string>.AsError(
                $"quality must be 1–100 (got {quality}).");
        }

        var formatResult = TryParseDesiredFormat(desiredFormat);
        if (formatResult.IsError)
            return Result<ConvertedImage, string>.AsError(formatResult.Error);

        var writeFormat = formatResult.Value;
        var canonical = CanonicalFormatId(desiredFormat);

        try
        {
            using MagickImage image = readFormat is { } format
                ? new MagickImage(imageBytes, new MagickReadSettings { Format = format })
                : new MagickImage(imageBytes);

            image.Quality = (uint)quality;
            image.Format = writeFormat;
            var bytes = image.ToByteArray();

            return Result<ConvertedImage, string>.AsValue(new ConvertedImage(
                bytes,
                (int)image.Width,
                (int)image.Height,
                canonical,
                quality));
        }
        catch (Exception ex) when (ex is MagickException or ArgumentException or ArgumentOutOfRangeException)
        {
            return Result<ConvertedImage, string>.AsError($"Image conversion failed: {ex.Message}");
        }
    }
}

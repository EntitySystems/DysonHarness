using ImageMagick;

namespace DysonHarness;

/// <summary>Decodes a generated image and emits a PNG artifact with its dimensions.</summary>
public static class DysonImageGenerationNormalize
{
    /// <summary>PNG bytes suitable for durable generated-image artifact storage.</summary>
    public readonly record struct NormalizedPng(byte[] Bytes, int Width, int Height)
    {
        public const string MimeType = "image/png";
    }

    /// <summary>
    /// Decodes image bytes (including JPEG and WebP) and re-encodes them as PNG.
    /// </summary>
    public static Result<NormalizedPng, string> ToPng(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            return Result<NormalizedPng, string>.AsError("Generated image bytes are empty.");

        try
        {
            using var image = new MagickImage(imageBytes);
            image.Format = MagickFormat.Png;
            var png = image.ToByteArray();

            return Result<NormalizedPng, string>.AsValue(new NormalizedPng(
                png,
                checked((int)image.Width),
                checked((int)image.Height)));
        }
        catch (Exception ex) when (ex is MagickException or ArgumentException or ArgumentOutOfRangeException)
        {
            return Result<NormalizedPng, string>.AsError(
                $"Generated image normalization failed: {ex.Message}");
        }
    }
}

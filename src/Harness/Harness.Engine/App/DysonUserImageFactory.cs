namespace DysonHarness;

/// <summary>
/// Builds compressed JPEG <see cref="DysonBinaryAttachment"/> values for composer user images.
/// </summary>
public static class DysonUserImageFactory
{
    public const int MaxPendingImages = 8;
    public const int MaxRawBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Decodes <paramref name="imageBytes"/>, compresses via <see cref="DysonImageCompress"/>,
    /// and returns a JPEG attachment (original basename, <c>.jpg</c> extension).
    /// </summary>
    public static Result<DysonBinaryAttachment, string> CreateFromBytes(
        string? fileName,
        byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            return Result<DysonBinaryAttachment, string>.AsError("Image is empty.");
        if (imageBytes.Length > MaxRawBytes)
            return Result<DysonBinaryAttachment, string>.AsError("Image is too large (max 25 MB).");

        try
        {
            var compressed = DysonImageCompress.ToJpegMaxEdge(imageBytes);
            var baseName = SanitizeFileName(fileName);
            return Result<DysonBinaryAttachment, string>.AsValue(new DysonBinaryAttachment
            {
                FileName = baseName + ".jpg",
                Extension = ".jpg",
                MimeType = compressed.MimeType,
                Base64Data = Convert.ToBase64String(compressed.Bytes),
            });
        }
        catch (Exception ex)
        {
            return Result<DysonBinaryAttachment, string>.AsError($"Could not decode image: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses a <c>data:image/…;base64,...</c> URL and builds a compressed attachment.
    /// </summary>
    public static Result<DysonBinaryAttachment, string> CreateFromDataUrl(
        string? fileName,
        string dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
            return Result<DysonBinaryAttachment, string>.AsError("Image data URL is empty.");

        var comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || comma >= dataUrl.Length - 1)
            return Result<DysonBinaryAttachment, string>.AsError("Invalid image data URL.");

        var header = dataUrl[..comma];
        if (!header.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || !header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return Result<DysonBinaryAttachment, string>.AsError("Only image data URLs are supported.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException ex)
        {
            return Result<DysonBinaryAttachment, string>.AsError($"Invalid image base64: {ex.Message}");
        }

        return CreateFromBytes(fileName, bytes);
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "image" : Path.GetFileNameWithoutExtension(fileName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            name = "image";

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        if (name.Length > 80)
            name = name[..80];

        return name;
    }
}

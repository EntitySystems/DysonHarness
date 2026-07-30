namespace DysonHarness;

/// <summary>
/// Writes composer-pasted/dropped non-image files under <c>.dyson/composer-uploads</c>
/// and returns a workspace-relative path for <c>AppendPathsToLastUser</c>.
/// </summary>
public static class DysonComposerUploads
{
    public const string RelativeDirectory = ".dyson/composer-uploads";
    public const int MaxPendingFiles = 8;
    public const int MaxRawBytes = DysonUserImageFactory.MaxRawBytes;

    /// <summary>
    /// True when <paramref name="contentType"/> is <c>image/*</c>, or when MIME is empty and
    /// <paramref name="fileName"/> has a common image extension (clipboard/drop often omit type).
    /// </summary>
    public static bool LooksLikeImage(string? contentType, string? fileName)
    {
        if (contentType is { Length: > 0 } mime
            && !string.IsNullOrWhiteSpace(mime))
            return mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        var ext = Path.GetExtension(fileName);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>image/*</c> content type from a common image extension; otherwise <c>application/octet-stream</c>.
    /// </summary>
    public static string ImageContentTypeFromFileName(string? fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            return "image/gif";
        if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            return "image/bmp";
        if (ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            return "image/tiff";
        if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            return "image/x-icon";
        return "application/octet-stream";
    }

    /// <summary>
    /// Ensures the upload dir exists, writes <paramref name="bytes"/> under a sanitized unique name,
    /// and returns the workspace-relative path (forward slashes).
    /// </summary>
    public static Result<string, string> Write(
        IDysonWorkspaceFileSystem fs,
        string? fileName,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
            return Result<string, string>.AsError("File is empty.");
        if (bytes.Length > MaxRawBytes)
            return Result<string, string>.AsError("File is too large (max 25 MB).");

        var ensureDir = fs.CreateDirectory(RelativeDirectory);
        if (ensureDir.IsError)
            return Result<string, string>.AsError(ensureDir.Error);

        var relative = AllocateRelativePath(fs, fileName);
        var written = fs.WriteAllBytes(relative, bytes);
        if (written.IsError)
            return Result<string, string>.AsError(written.Error);

        return Result<string, string>.AsValue(relative);
    }

    private static string AllocateRelativePath(IDysonWorkspaceFileSystem fs, string? fileName)
    {
        var safeName = SanitizeFileName(fileName);
        var relative = $"{RelativeDirectory}/{safeName}";
        var exists = fs.FileExists(relative);
        if (exists.IsError || !exists.Value)
            return relative;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        for (var i = 1; i < 10_000; i++)
        {
            relative = $"{RelativeDirectory}/{stem}-{i}{ext}";
            exists = fs.FileExists(relative);
            if (exists.IsError || !exists.Value)
                return relative;
        }

        // ponytail: absurd collision ceiling; GUID suffix if somehow exhausted.
        return $"{RelativeDirectory}/{stem}-{Guid.NewGuid():N}{ext}";
    }

    private static string SanitizeFileName(string? fileName)
    {
        var trimmed = string.IsNullOrWhiteSpace(fileName) ? "file" : Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            trimmed = "file";

        var stem = Path.GetFileNameWithoutExtension(trimmed);
        var ext = Path.GetExtension(trimmed);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "file";

        foreach (var c in Path.GetInvalidFileNameChars())
            stem = stem.Replace(c, '_');

        if (stem.Length > 80)
            stem = stem[..80];

        if (!string.IsNullOrEmpty(ext))
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                ext = ext.Replace(c, '_');
            if (ext.Length > 32)
                ext = ext[..32];
        }

        return stem + ext;
    }
}

namespace DysonHarness;

/// <summary>
/// Writes composer-pasted/dropped files and compressed images under <c>.dyson/composer-uploads</c>
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
        return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tiff", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>image/*</c> content type from a common image extension; otherwise <c>application/octet-stream</c>.
    /// </summary>
    public static string ImageContentTypeFromFileName(string? fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";
        if (string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";
        if (string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase))
            return "image/gif";
        if (string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase))
            return "image/bmp";
        if (string.Equals(ext, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tiff", StringComparison.OrdinalIgnoreCase))
            return "image/tiff";
        if (string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase))
            return "image/x-icon";
        return "application/octet-stream";
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> is exactly <see cref="RelativeDirectory"/>
    /// (slash-normalized, trailing slash ignored).
    /// </summary>
    public static bool IsComposerUploadsDirectory(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Trim().Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalized, RelativeDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> is <see cref="RelativeDirectory"/> or a child of it.
    /// </summary>
    public static bool IsUnderComposerUploads(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (string.Equals(normalized, RelativeDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = RelativeDirectory + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures the upload dir exists, writes <paramref name="bytes"/> under a sanitized unique name,
    /// and returns the workspace-relative path (forward slashes).
    /// </summary>
    public static async Task<Result<string, string>> WriteAsync(
        IDysonWorkspaceFileSystem fs,
        string? fileName,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
            return Result<string, string>.AsError("File is empty.");
        if (bytes.Length > MaxRawBytes)
            return Result<string, string>.AsError("File is too large (max 25 MB).");

        var ensureDir = await fs.CreateDirectoryAsync(RelativeDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (ensureDir.IsError)
            return Result<string, string>.AsError(ensureDir.Error);

        var relative = await AllocateRelativePathAsync(fs, fileName, cancellationToken)
            .ConfigureAwait(false);
        var written = await fs.WriteAllBytesAsync(relative, bytes, cancellationToken)
            .ConfigureAwait(false);
        if (written.IsError)
            return Result<string, string>.AsError(written.Error);

        return Result<string, string>.AsValue(relative);
    }

    /// <summary>
    /// Deletes all files and subdirectories under <see cref="RelativeDirectory"/>, keeping the folder.
    /// Creates the directory if missing. Returns the number of direct children removed.
    /// </summary>
    public static async Task<Result<int, string>> ClearAllAsync(
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);

        var exists = await fs.DirectoryExistsAsync(RelativeDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (exists.IsError)
            return Result<int, string>.AsError(exists.Error);

        if (!exists.Value)
        {
            var created = await fs.CreateDirectoryAsync(RelativeDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
                return Result<int, string>.AsError(created.Error);
            return Result<int, string>.AsValue(0);
        }

        var entries = await fs.EnumerateEntriesAsync(RelativeDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (entries.IsError)
            return Result<int, string>.AsError(entries.Error);

        var deleted = 0;
        foreach (var entry in entries.Value)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var child = $"{RelativeDirectory}/{entry.Name}";
            var removed = entry.IsDirectory
                ? await fs.DeleteDirectoryAsync(child, recursive: true, cancellationToken)
                    .ConfigureAwait(false)
                : await fs.DeleteFileAsync(child, cancellationToken)
                    .ConfigureAwait(false);
            if (removed.IsError)
                return Result<int, string>.AsError(removed.Error);

            deleted++;
        }

        return Result<int, string>.AsValue(deleted);
    }

    private static async Task<string> AllocateRelativePathAsync(
        IDysonWorkspaceFileSystem fs,
        string? fileName,
        CancellationToken cancellationToken)
    {
        var safeName = SanitizeFileName(fileName);
        var relative = $"{RelativeDirectory}/{safeName}";
        var exists = await fs.FileExistsAsync(relative, cancellationToken).ConfigureAwait(false);
        if (exists.IsError || !exists.Value)
            return relative;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        for (var i = 1; i < 10_000; i++)
        {
            relative = $"{RelativeDirectory}/{stem}-{i}{ext}";
            exists = await fs.FileExistsAsync(relative, cancellationToken).ConfigureAwait(false);
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

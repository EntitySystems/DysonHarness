using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DysonHarness;

/// <summary>One zip member. <see cref="FullName"/> is '/'-separated with no trailing slash.</summary>
public sealed record DysonZipEntry(string FullName, string Name, bool IsDirectory, long Length, DateTimeOffset? LastWriteTime);

/// <summary>A single entry extracted into the cache. <see cref="Root"/> + <see cref="RelativePath"/> feed the file viewer.</summary>
public sealed record DysonZipExtracted(string Root, string RelativePath, string AbsolutePath, bool OpenInViewer);

/// <summary>
/// Read-only ZIP browsing for the file viewer: entry listing, folder walking, and cached
/// single-entry extraction (zip-slip checked, size capped). BCL <c>System.IO.Compression</c> only.
/// </summary>
public static class DysonZipArchives
{
    public const long DefaultMaxEntryBytes = 512L * 1024 * 1024;

    private const int MaxEntryDepth = 64;
    private const int CopyBufferSize = 64 * 1024;
    private const int SniffBytes = 8 * 1024;

    public static string DefaultCacheRoot => Path.Combine(Path.GetTempPath(), "dyson-zip-cache");

    public static bool IsZipPath(string? path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lists file entries plus explicit directory entries (so empty folders show). Entries whose
    /// path is unsafe (rooted, <c>..</c>, drive, control chars) are skipped.
    /// </summary>
    public static Task<Result<IReadOnlyList<DysonZipEntry>, string>> ReadEntriesAsync(
        string archivePath,
        CancellationToken ct = default) =>
        Task.Run(() => ReadEntries(archivePath, ct), ct);

    /// <summary>Pure folder walker: direct children of <paramref name="folder"/> ("" = root), folders first, each group sorted.</summary>
    public static IReadOnlyList<DysonZipEntry> ListFolder(IReadOnlyList<DysonZipEntry> files, string folder)
    {
        folder = (folder ?? "").Trim('/');
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var folders = new Dictionary<string, DysonZipEntry>(StringComparer.Ordinal);
        var direct = new List<DysonZipEntry>();

        foreach (var entry in files)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rest = entry.FullName[prefix.Length..];
            if (rest.Length == 0)
                continue;

            var slash = rest.IndexOf('/');
            if (slash >= 0 || entry.IsDirectory)
            {
                var segment = slash >= 0 ? rest[..slash] : rest;
                var fullName = prefix + segment;
                folders.TryAdd(fullName, new DysonZipEntry(fullName, segment, true, 0, null));
            }
            else
            {
                direct.Add(entry);
            }
        }

        return
        [
            .. folders.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            .. direct.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public static string ParentFolder(string folder)
    {
        var trimmed = (folder ?? "").Trim('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? "" : trimmed[..slash];
    }

    /// <summary>
    /// Extracts ONE entry into <c>{cacheRoot}/{key}/{leaf}</c>; key = SHA256(archive path|mtime|length|entry).
    /// Re-opening an unchanged archive entry is a cache hit (no write).
    /// </summary>
    public static Task<Result<DysonZipExtracted, string>> ExtractEntryAsync(
        string archivePath,
        string entryFullName,
        string? cacheRoot = null,
        long maxEntryBytes = DefaultMaxEntryBytes,
        CancellationToken ct = default) =>
        Task.Run(() => ExtractEntry(archivePath, entryFullName, cacheRoot ?? DefaultCacheRoot, maxEntryBytes, ct), ct);

    private static Result<IReadOnlyList<DysonZipEntry>, string> ReadEntries(string archivePath, CancellationToken ct)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var list = new List<DysonZipEntry>(archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('\\', '/');
                var isDirectory = normalized.EndsWith('/');
                if (ValidateEntryPath(normalized).IsError)
                    continue;

                var fullName = normalized.TrimEnd('/');
                var name = fullName[(fullName.LastIndexOf('/') + 1)..];
                list.Add(new DysonZipEntry(
                    fullName,
                    name,
                    isDirectory,
                    isDirectory ? 0 : entry.Length,
                    entry.LastWriteTime));
            }

            return Result<IReadOnlyList<DysonZipEntry>, string>.AsValue(list);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<DysonZipEntry>, string>.AsError($"Not a valid ZIP archive: {ex.Message}");
        }
    }

    private static Result<DysonZipExtracted, string> ExtractEntry(
        string archivePath,
        string entryFullName,
        string cacheRoot,
        long maxEntryBytes,
        CancellationToken ct)
    {
        var unsafeMessage = $"Unsafe zip entry path: '{entryFullName}'.";
        var safe = ValidateEntryPath(entryFullName);
        if (safe.IsError)
            return Result<DysonZipExtracted, string>.AsError(unsafeMessage);

        string? tmp = null;
        try
        {
            var info = new FileInfo(archivePath);
            if (!info.Exists)
                return Result<DysonZipExtracted, string>.AsError($"Archive not found: '{archivePath}'.");

            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{entryFullName}"))).ToLowerInvariant()[..32];
            var cacheDir = Path.Combine(cacheRoot, key);
            var leaf = safe.Value[(safe.Value.LastIndexOf('/') + 1)..];
            var resolved = DysonPluginPackageSecurity.ResolveContainedPath(cacheDir, leaf);
            if (resolved.IsError)
                return Result<DysonZipExtracted, string>.AsError(unsafeMessage);
            var target = resolved.Value;

            // Look up by the raw listing name (ReadEntries keeps it un-normalized, e.g. NFD from macOS);
            // safe.Value (NFC) is only for the safety check and the on-disk leaf.
            var lookupName = entryFullName.Replace('\\', '/');
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries.FirstOrDefault(e =>
                !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\') &&
                string.Equals(e.FullName.Replace('\\', '/'), lookupName, StringComparison.Ordinal));
            if (entry is null)
                return Result<DysonZipExtracted, string>.AsError($"Zip entry not found: '{entryFullName}'.");
            var tooLarge = Result<DysonZipExtracted, string>.AsError($"Entry is larger than {FormatLimit(maxEntryBytes)}.");
            if (entry.Length > maxEntryBytes)
                return tooLarge;

            var hit = File.Exists(target) && new FileInfo(target).Length == entry.Length;
            if (!hit)
            {
                Directory.CreateDirectory(cacheDir);
                tmp = $"{target}.{Guid.NewGuid():N}.tmp";
                using (var input = entry.Open())
                using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[CopyBufferSize];
                    long copied = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        copied += read;
                        if (copied > maxEntryBytes)
                            return tooLarge; // finally deletes tmp
                        output.Write(buffer, 0, read);
                    }
                }

                File.Move(tmp, target, overwrite: true);
                tmp = null;
            }

            return Result<DysonZipExtracted, string>.AsValue(
                new DysonZipExtracted(cacheDir, leaf, target, ShouldOpenInViewer(target)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Result<DysonZipExtracted, string>.AsError($"Failed to extract '{entryFullName}': {ex.Message}");
        }
        finally
        {
            if (tmp is not null)
            {
                try { File.Delete(tmp); }
                catch { /* best-effort temp cleanup */ }
            }
        }
    }

    // Reuses the plugin-package zip-slip guard; callers map any failure to their own message.
    private static Result<string, string> ValidateEntryPath(string path) =>
        DysonPluginPackageSecurity.ValidateRelativePath(path, MaxEntryDepth);

    private static bool ShouldOpenInViewer(string path)
    {
        if (IsZipPath(path)
            || string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
            || DysonComposerUploads.LooksLikeImage(null, path)
            || DysonAudioFiles.IsAudioPath(path))
            return true;

        using var stream = File.OpenRead(path);
        var buffer = new byte[SniffBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read).IndexOf((byte)0) < 0;
    }

    private static string FormatLimit(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MiB" : $"{bytes} bytes";

    // ponytail: no cache eviction; stale key dirs stay in OS temp. Upgrade: prune dirs older than N days on first use.
}

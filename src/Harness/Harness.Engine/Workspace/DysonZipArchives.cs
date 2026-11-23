using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DysonHarness;

/// <summary>One zip member. <see cref="FullName"/> is '/'-separated with no trailing slash.</summary>
public sealed record DysonZipEntry(string FullName, string Name, bool IsDirectory, long Length, DateTimeOffset? LastWriteTime);

/// <summary>
/// A single entry extracted into the cache. <see cref="Root"/> + <see cref="RelativePath"/> feed the file viewer.
/// <see cref="CanLaunch"/> is false for executables/scripts (see <see cref="DysonZipArchives.IsLaunchBlocked"/>):
/// the viewer shows them read-only and never offers the OS default handler.
/// </summary>
public sealed record DysonZipExtracted(string Root, string RelativePath, string AbsolutePath, bool OpenInViewer, bool CanLaunch);

/// <summary>
/// Read-only ZIP browsing for the file viewer: entry listing, folder walking, and cached
/// single-entry extraction (zip-slip checked, size capped). BCL <c>System.IO.Compression</c> only.
/// </summary>
public static class DysonZipArchives
{
    /// <summary>Cap for entries opened in the in-memory viewer (text, PDF, image, audio, nested zip).</summary>
    public const long MaxViewerEntryBytes = 64L * 1024 * 1024;

    /// <summary>Cap for entries handed to the OS default handler.</summary>
    public const long MaxLauncherEntryBytes = 512L * 1024 * 1024;

    /// <summary><see cref="ReadEntriesAsync"/> refuses archives with more entries than this.</summary>
    public const int MaxEntries = 100_000;

    private const int MaxEntryDepth = 64;
    private const int CopyBufferSize = 64 * 1024;
    private const int UnixExecBits = 0b001_001_001; // --x--x--x in the zip's Unix mode (external attributes >> 16)
    private const UnixFileMode ExecModes = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Per-user cache (<c>LocalApplicationData/DysonHarness/zip-cache</c>); OS temp only when that folder is unknown.</summary>
    public static string DefaultCacheRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local)
                ? Path.Combine(Path.GetTempPath(), "dyson-zip-cache")
                : Path.Combine(local, "DysonHarness", "zip-cache");
        }
    }

    public static bool IsZipPath(string? path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when an entry must never reach the OS launcher: an executable/script extension (case-insensitive,
    /// trailing dots/spaces ignored as Windows does) or a Unix exec bit in the zip's external attributes.
    /// The extension denylist lives in <see cref="DysonFileRouting"/>.
    /// </summary>
    public static bool IsLaunchBlocked(string entryName, int externalAttributes = 0) =>
        DysonFileRouting.IsLaunchBlockedName(entryName) || ExecBit(externalAttributes);

    private static bool ExecBit(int externalAttributes) => ((externalAttributes >> 16) & UnixExecBits) != 0;

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
    /// Re-opening an unchanged archive entry is a cache hit (no write). The route is decided before anything is
    /// written: launcher-bound entries that <see cref="IsLaunchBlocked"/> are refused. Cap: <paramref name="maxEntryBytes"/>,
    /// else <see cref="MaxViewerEntryBytes"/> / <see cref="MaxLauncherEntryBytes"/> by route. Windows: Mark-of-the-Web.
    /// </summary>
    public static Task<Result<DysonZipExtracted, string>> ExtractEntryAsync(
        string archivePath,
        string entryFullName,
        string? cacheRoot = null,
        long? maxEntryBytes = null,
        CancellationToken ct = default) =>
        Task.Run(() => ExtractEntry(archivePath, entryFullName, cacheRoot ?? DefaultCacheRoot, maxEntryBytes, ct), ct);

    private static Result<IReadOnlyList<DysonZipEntry>, string> ReadEntries(string archivePath, CancellationToken ct)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > MaxEntries)
                return Result<IReadOnlyList<DysonZipEntry>, string>.AsError(
                    $"Archive has more than {MaxEntries:N0} entries; too many to list.");
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
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return Result<IReadOnlyList<DysonZipEntry>, string>.AsError($"Not a valid ZIP archive: {ex.Message}");
        }
    }

    private static Result<DysonZipExtracted, string> ExtractEntry(
        string archivePath,
        string entryFullName,
        string cacheRoot,
        long? maxEntryBytes,
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

            // Route before anything is written: viewer types by name, else the shared classifier on the first 8 KiB.
            var head = ReadHead(entry);
            var route = DysonFileRouting.Classify(entryFullName, head, ExecBit(entry.ExternalAttributes));
            var openInViewer = IsViewerTypeByName(leaf)
                || route is DysonFileOpenRoute.Viewer or DysonFileOpenRoute.ViewerReadOnly;
            var canLaunch = route is DysonFileOpenRoute.Viewer or DysonFileOpenRoute.OsDefault;
            var refused = Result<DysonZipExtracted, string>.AsError(
                $"Refusing to open '{leaf}': executable and script files from archives are blocked.");
            if (!openInViewer && !canLaunch)
                return refused;

            var cap = maxEntryBytes ?? (openInViewer ? MaxViewerEntryBytes : MaxLauncherEntryBytes);
            var tooLarge = Result<DysonZipExtracted, string>.AsError(
                $"'{leaf}' is larger than the {FormatLimit(cap)} limit for {(openInViewer ? "the viewer" : "the default app")}.");
            if (entry.Length > cap)
                return tooLarge;

            if (!IsCachedCopy(target, entry.Length))
            {
                if (!OperatingSystem.IsWindows())
                    Directory.CreateDirectory(cacheRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
                        if (copied > cap)
                            return tooLarge; // finally deletes tmp
                        output.Write(buffer, 0, read);
                    }
                }

                try
                {
                    File.Move(tmp, target, overwrite: true);
                    tmp = null;
                }
                catch (IOException) when (IsCachedCopy(target, entry.Length))
                {
                    // Another reader holds the cached copy open; it is this same entry, so use it (finally drops tmp).
                }
            }

            if (canLaunch && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(target) & ExecModes) != 0)
                canLaunch = false;
            if (!openInViewer && !canLaunch)
                return refused;

            WriteMarkOfTheWeb(target);
            return Result<DysonZipExtracted, string>.AsValue(
                new DysonZipExtracted(cacheDir, leaf, target, openInViewer, canLaunch));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
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

    private static bool IsViewerTypeByName(string path) =>
        IsZipPath(path)
        || string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
        || DysonComposerUploads.LooksLikeImage(null, path)
        || DysonAudioFiles.IsAudioPath(path)
        || DysonVideoFiles.IsVideoPath(path);

    private static byte[] ReadHead(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var buffer = new byte[DysonFileRouting.SniffBytes];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return buffer[..read];
    }

    // A symlink planted at the target is a miss, so it gets replaced by a real extracted file.
    private static bool IsCachedCopy(string path, long length)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null && info.Length == length;
    }

    // Best-effort Mark-of-the-Web (Internet zone) so SmartScreen and Office Protected View apply to the copy.
    private static void WriteMarkOfTheWeb(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Non-NTFS volume or locked file: open anyway.
        }
    }

    private static string FormatLimit(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MiB" : $"{bytes} bytes";

    // ponytail: no cache eviction; stale key dirs stay in the cache root. Upgrade: prune dirs older than N days on first use.
}

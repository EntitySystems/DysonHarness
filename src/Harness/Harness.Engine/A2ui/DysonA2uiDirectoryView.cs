namespace DysonHarness;

public enum DysonA2uiEntryKind { Folder, File, Image, Video, Audio, Pdf, Archive, Model3D }

public enum DysonA2uiDirectorySort { Name, Size, Modified }

/// <summary>One row of a DirectoryView listing. <see cref="Blocked"/> = a link whose real target leaves the work root.</summary>
public sealed record DysonA2uiDirectoryEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    DysonA2uiEntryKind Kind,
    long? Size,
    DateTime ModifiedUtc,
    bool IsLink,
    bool Blocked)
{
    /// <summary>The built-in func a file click dispatches (files only): the same file viewer routing as chat links.</summary>
    public string OpenFunc => DysonBuiltInConversationActions.OpenFilePrefix + RelativePath;
}

/// <summary>A shallow folder listing. <see cref="RelativePath"/> is "" for the work root.</summary>
public sealed record DysonA2uiDirectoryListing(
    string RelativePath,
    string Name,
    IReadOnlyList<DysonA2uiDirectoryEntry> Entries,
    int TotalSeen,
    bool Truncated)
{
    public string OpenFolderFunc =>
        DysonBuiltInConversationActions.OpenFolderPrefix + (RelativePath.Length == 0 ? "." : RelativePath);

    public string? ParentRelativePath => RelativePath.Length == 0 ? null : DysonA2uiDirectoryView.ParentOf(RelativePath);

    /// <summary>The work root (named after its folder) plus one item per path segment; the last item is this folder.</summary>
    public IReadOnlyList<(string Name, string RelativePath)> Breadcrumb { get; init; } = [];
}

/// <summary>
/// Dyson-catalog <c>DirectoryView</c> (<c>path</c>, optional <c>title</c>, <c>height</c>): a work-relative folder listing.
/// <c>path</c> follows the <c>open_folder:</c> lexical rules; every load also resolves the real path (links on every segment)
/// and requires it to sit under the real work root. Listing is a snapshot: nothing from it is persisted.
/// </summary>
public static class DysonA2uiDirectoryView
{
    public const string ComponentName = "DirectoryView";

    public const int MaxEntries = 1000;
    public const int ScanCap = 5000;
    public const int DefaultHeight = 240;
    public const int RowPx = 28;
    public const int HeaderPx = 52;

    /// <summary>Work-relative folder validated exactly as <c>open_folder:</c> validates it ("." becomes "").</summary>
    public static async Task<Result<string, string>> ResolveRelativeAsync(
        string? path,
        string? workRoot,
        CancellationToken ct = default)
    {
        var resolved = await DysonBuiltInConversationActions
            .TryResolveAsync(DysonBuiltInConversationActions.OpenFolderPrefix + (path ?? ""), workRoot, ct)
            .ConfigureAwait(false);
        return resolved switch
        {
            null => Result<string, string>.AsError("Folder path is required."),
            { IsError: true } failed => Result<string, string>.AsError(failed.Error),
            { } ok => Result<string, string>.AsValue(ok.Value.PathOrUrl ?? ""),
        };
    }

    /// <summary>
    /// The folder's entries (default order: folders first, names OrdinalIgnoreCase then Ordinal), or the text a card shows instead
    /// (escaping path, missing folder, a file, unreadable). Null or blank <paramref name="relativePath"/> is the work root.
    /// All IO runs on the thread pool.
    /// </summary>
    public static async Task<Result<DysonA2uiDirectoryListing, string>> LoadAsync(
        string? relativePath,
        string? workRoot,
        CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var rel = await ResolveRelativeAsync(string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath, workRoot, ct).ConfigureAwait(false);
                return rel.IsError
                    ? Result<DysonA2uiDirectoryListing, string>.AsError(rel.Error)
                    : Scan(rel.Value, workRoot!, ct);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<DysonA2uiDirectoryListing, string>.AsError("Cancelled.");
        }
    }

    private static Result<DysonA2uiDirectoryListing, string> Scan(string rel, string workRoot, CancellationToken ct)
    {
        var shown = rel.Length == 0 ? "." : rel;
        var real = DysonOpenFolder.ResolveReal(rel, workRoot, forLaunch: false);
        if (real.IsError)
            return Result<DysonA2uiDirectoryListing, string>.AsError(real.Error);

        try
        {
            var root = Path.GetFullPath(workRoot.Trim());
            var realRoot = DysonOpenFolder.RealPath(root);

            // AttributesToSkip = 0: hidden and system entries (a Windows-hidden .git) show, like the file tree.
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            var seen = new List<FileSystemInfo>();
            var capped = false;
            foreach (var info in new DirectoryInfo(real.Value).EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (seen.Count == ScanCap)
                {
                    capped = true;
                    break;
                }

                // ponytail: a name the open_file:/open_folder: key cannot carry back (edge whitespace is trimmed there, a Unix
                // backslash is turned into '/') would list but never open, so it is left out. Upgrade: a path-free entry id.
                if (info.Name.Trim() != info.Name || (!OperatingSystem.IsWindows() && info.Name.Contains('\\')))
                    continue;
                seen.Add(info);
            }

            // ponytail: with more than ScanCap entries the sorted top MaxEntries come from the first ScanCap scanned.
            seen.Sort((a, b) =>
            {
                var aDir = a is DirectoryInfo;
                return aDir != (b is DirectoryInfo) ? (aDir ? -1 : 1) : CompareNames(a.Name, b.Name);
            });

            var entries = new List<DysonA2uiDirectoryEntry>(Math.Min(seen.Count, MaxEntries));
            foreach (var info in seen.Take(MaxEntries))
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(ToEntry(info, rel, realRoot));
            }

            var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            if (rootName.Length == 0)
                rootName = root;
            var crumbs = new List<(string, string)> { (rootName, "") };
            var acc = "";
            foreach (var segment in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = acc.Length == 0 ? segment : acc + "/" + segment;
                crumbs.Add((segment, acc));
            }

            return Result<DysonA2uiDirectoryListing, string>.AsValue(
                new DysonA2uiDirectoryListing(rel, crumbs[^1].Item1, entries, seen.Count, capped || seen.Count > MaxEntries)
                {
                    Breadcrumb = crumbs,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result<DysonA2uiDirectoryListing, string>.AsError($"Cannot read folder {shown}: {ex.Message}");
        }
    }

    private static DysonA2uiDirectoryEntry ToEntry(FileSystemInfo info, string rel, string realRoot)
    {
        var isDir = info is DirectoryInfo;
        var relative = rel.Length == 0 ? info.Name : rel + "/" + info.Name;

        var isLink = false;
        var blocked = false;
        try
        {
            isLink = info.LinkTarget is not null;
            if (isLink)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                blocked = target is null || !DysonOpenFolder.IsUnder(realRoot, DysonOpenFolder.RealPath(target.FullName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            blocked = true; // a link that cannot be resolved safely fails closed
        }

        long? size = null;
        if (info is FileInfo file)
        {
            try { size = file.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* dangling link or unreadable: no size */ }
        }

        return new DysonA2uiDirectoryEntry(
            info.Name, relative, isDir, isDir ? DysonA2uiEntryKind.Folder : KindOf(info.Name),
            size, info.LastWriteTimeUtc, isLink, blocked);
    }

    /// <summary>Kind by file name only (no sniffing), through the existing classifiers.</summary>
    public static DysonA2uiEntryKind KindOf(string name) =>
        DysonVideoFiles.ContentTypeFor(name) is not null ? DysonA2uiEntryKind.Video
        : DysonAudioFiles.ContentTypeFor(name) is not null ? DysonA2uiEntryKind.Audio
        : DysonA2uiImage.ContentTypeFor(name) is not null ? DysonA2uiEntryKind.Image
        : DysonA2uiModel3D.ContentTypeFor(name) is not null ? DysonA2uiEntryKind.Model3D
        : DysonZipArchives.IsZipPath(name) ? DysonA2uiEntryKind.Archive
        : string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase) ? DysonA2uiEntryKind.Pdf
        : DysonA2uiEntryKind.File;

    private static int CompareNames(string a, string b)
    {
        var c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// Folders first, then <paramref name="key"/> (reversed when <paramref name="descending"/>); ties fall back to the name
    /// ascending, so the order is total and stable in both directions.
    /// </summary>
    public static IReadOnlyList<DysonA2uiDirectoryEntry> Sort(
        IReadOnlyList<DysonA2uiDirectoryEntry> entries,
        DysonA2uiDirectorySort key,
        bool descending) =>
        entries.Order(Comparer<DysonA2uiDirectoryEntry>.Create((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;
            var c = key switch
            {
                DysonA2uiDirectorySort.Size => Nullable.Compare(a.Size, b.Size),
                DysonA2uiDirectorySort.Modified => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
                _ => CompareNames(a.Name, b.Name),
            };
            if (descending)
                c = -c;
            return c != 0 ? c : CompareNames(a.Name, b.Name);
        })).ToList();

    /// <summary>Rows the inline card shows: <c>(clamp(height, 120, 800) - HeaderPx) / RowPx</c>, at least 1.</summary>
    public static int VisibleRows(int height) => Math.Max(1, (Math.Clamp(height, 120, 800) - HeaderPx) / RowPx);

    /// <summary>Lexical parent of a work-relative path; "" at the root.</summary>
    public static string ParentOf(string relativePath)
    {
        var trimmed = relativePath.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i < 0 ? "" : trimmed[..i];
    }
}

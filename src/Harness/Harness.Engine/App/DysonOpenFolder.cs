namespace DysonHarness;

/// <summary>
/// Click-time half of the <c>open_folder:{path}</c> built-in action. The key was already vetted lexically
/// (<see cref="DysonBuiltInConversationActions"/>: relative, no <c>..</c>); here the folder must exist, must not be
/// a file, and its real path (symlinks / junctions resolved on every segment) must still sit under the real work root.
/// Only then is it handed to the launcher (<see cref="DysonOsLauncher.OpenFolderAsync"/> by default: argument list, no shell).
/// </summary>
public static class DysonOpenFolder
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <param name="relativePath">Work-relative folder; "" or "." is the work root.</param>
    /// <param name="launch">Launcher seam (tests pass a recorder). Null uses the OS launcher.</param>
    public static async Task<VoidResult<string>> OpenAsync(
        string? relativePath,
        string? workRoot,
        Func<string, Task<VoidResult<string>>>? launch = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await Task.Run(() => ResolveReal(relativePath, workRoot), cancellationToken).ConfigureAwait(false);
        if (resolved.IsError)
            return VoidResult<string>.AsError(resolved.Error);

        return await (launch ?? DysonOsLauncher.OpenFolderAsync)(resolved.Value).ConfigureAwait(false);
    }

    /// <summary>The real absolute folder path, or the reason nothing may be opened. Does IO: call off the circuit.</summary>
    public static Result<string, string> ResolveReal(string? relativePath, string? workRoot)
    {
        if (string.IsNullOrWhiteSpace(workRoot))
            return Result<string, string>.AsError("Work directory is required.");

        var relative = relativePath?.Trim() ?? "";
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
            return Result<string, string>.AsError($"Path escapes work directory: {relative}");

        try
        {
            var root = Path.GetFullPath(workRoot.Trim());
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (File.Exists(full))
                return Result<string, string>.AsError($"Not a folder: {Display(relative)}");
            if (!Directory.Exists(full))
                return Result<string, string>.AsError($"Folder not found: {Display(relative)}");

            var realRoot = RealPath(root);
            var realFull = RealPath(full);
            return IsUnder(realRoot, realFull)
                ? Result<string, string>.AsValue(realFull)
                : Result<string, string>.AsError($"Path escapes work directory: {Display(relative)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result<string, string>.AsError($"Cannot open folder: {ex.Message}");
        }
    }

    private static string Display(string relative) => relative.Length == 0 ? "." : relative;

    /// <summary>Resolves a link on every segment (a junction in the middle of the path counts, not just the last one).</summary>
    private static string RealPath(string full, int depth = 0)
    {
        // A link target can itself pass through links, so resolve it again; 16 hops is far past any real chain.
        if (depth > 16)
            throw new IOException("Too many symbolic links.");

        var current = Path.GetPathRoot(full) ?? "";
        var rest = full[current.Length..];
        foreach (var segment in rest.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = RealPath(target.FullName, depth + 1);
        }

        return current;
    }

    private static bool IsUnder(string root, string path)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path, root, PathComparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }
}

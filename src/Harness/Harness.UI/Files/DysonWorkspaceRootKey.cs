namespace Harness.UI.Files;

/// <summary>
/// Cache key for operator surfaces that isolate a work directory id plus a session workspace root
/// (registered checkout vs git worktree).
/// </summary>
internal readonly record struct DysonWorkspaceRootKey(Guid WorkDirectoryId, string NormalizedPath)
{
    public static DysonWorkspaceRootKey From(Guid workDirectoryId, string absolutePath) =>
        new(workDirectoryId, Normalize(absolutePath));

    /// <summary>
    /// Full path, trailing separators trimmed, uppercased on Windows so dictionary keys match
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    public static string Normalize(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var full = Path.GetFullPath(absolutePath.Trim());
        if (full.Length > 1)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        try
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

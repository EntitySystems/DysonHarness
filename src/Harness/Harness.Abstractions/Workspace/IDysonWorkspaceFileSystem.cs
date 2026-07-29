namespace DysonHarness;

/// <summary>
/// Sandboxed workspace filesystem. Call <see cref="InitializeAsync"/> before IO or watcher creation.
/// <see cref="NativeRootPath"/> is always the host-visible root for shells / <c>git -C</c>.
/// </summary>
public interface IDysonWorkspaceFileSystem
{
    /// <summary>Host-visible absolute root (local path, mapped drive, or UNC).</summary>
    string NativeRootPath { get; }

    /// <summary>Subject passed to a successful <see cref="InitializeAsync"/>; null until initialized.</summary>
    string? SubjectId { get; }

    bool IsInitialized { get; }

    /// <summary>
    /// Prepares the FS for IO. Local implementations accept only
    /// <see cref="DysonWorkspaceSubjects.LocalFs"/>. Idempotent for the same subject;
    /// changing subject after init fails.
    /// </summary>
    Task<VoidResult<string>> InitializeAsync(
        string subjectId,
        CancellationToken cancellationToken = default);

    Result<string, string> ResolvePath(string path);

    /// <summary>Workspace-relative path with forward slashes (empty string for the root).</summary>
    Result<string, string> GetRelativePath(string path);

    Result<bool, string> FileExists(string path);

    Result<bool, string> DirectoryExists(string path);

    Result<long, string> GetFileLength(string path);

    Result<string, string> ReadAllText(string path);

    Result<byte[], string> ReadAllBytes(string path);

    /// <summary>Reads up to <paramref name="maxBytes"/> from the start of the file.</summary>
    Result<byte[], string> ReadFileHead(string path, int maxBytes);

    VoidResult<string> WriteAllText(string path, string contents);

    VoidResult<string> WriteAllBytes(string path, byte[] contents);

    VoidResult<string> CreateDirectory(string path);

    /// <summary>Shallow listing of direct children (name + is-directory).</summary>
    Result<IReadOnlyList<DysonWorkspaceEntry>, string> EnumerateEntries(string path);

    /// <summary>
    /// Enumerates file paths under <paramref name="directoryPath"/> matching
    /// <paramref name="searchPattern"/> (e.g. <c>*.cs</c>). Returns absolute native paths.
    /// </summary>
    Result<IReadOnlyList<string>, string> EnumerateFiles(
        string directoryPath,
        string searchPattern = "*",
        bool recursive = false);

    VoidResult<string> DeleteFile(string path);

    VoidResult<string> DeleteDirectory(string path, bool recursive = false);

    /// <summary>
    /// Moves or renames a file or directory within the sandbox.
    /// Destination must not already exist; directory sources cannot move into themselves.
    /// </summary>
    VoidResult<string> Move(string sourceRelativePath, string destinationRelativePath);

    /// <summary>Creates a watcher for this initialized FS (does not start it).</summary>
    Result<IDysonWorkspaceChangeWatcher, string> CreateWatcher();
}

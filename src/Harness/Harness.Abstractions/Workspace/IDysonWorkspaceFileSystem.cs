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

    Task<Result<bool, string>> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<bool, string>> DirectoryExistsAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<long, string>> GetFileLengthAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<string, string>> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<byte[], string>> ReadAllBytesAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Reads up to <paramref name="maxBytes"/> from the start of the file.</summary>
    Task<Result<byte[], string>> ReadFileHeadAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a 1-based line window without loading the rest of the file.
    /// Negative <paramref name="startLine"/> tails from EOF. <c>0</c> is treated as <c>1</c>.
    /// </summary>
    Task<Result<DysonWorkspaceLineSlice, string>> ReadLineSliceAsync(
        string path,
        int startLine,
        int? maxLines,
        int maxChars,
        int maxLineChars,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> WriteAllBytesAsync(
        string path,
        byte[] contents,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Shallow listing of direct children (name + is-directory).</summary>
    Task<Result<IReadOnlyList<DysonWorkspaceEntry>, string>> EnumerateEntriesAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates file paths under <paramref name="directoryPath"/> matching
    /// <paramref name="searchPattern"/> (e.g. <c>*.cs</c>). Returns absolute native paths.
    /// </summary>
    Task<Result<IReadOnlyList<string>, string>> EnumerateFilesAsync(
        string directoryPath,
        string searchPattern = "*",
        bool recursive = false,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> DeleteDirectoryAsync(
        string path,
        bool recursive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves or renames a file or directory within the sandbox.
    /// Destination must not already exist; directory sources cannot move into themselves.
    /// </summary>
    Task<VoidResult<string>> MoveAsync(
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a watcher for this initialized FS (does not start it).</summary>
    Result<IDysonWorkspaceChangeWatcher, string> CreateWatcher();
}

namespace DysonHarness;

/// <summary>Factories for <see cref="IDysonWorkspaceFileSystem"/> implementations.</summary>
public static class DysonWorkspaceFileSystems
{
    /// <summary>
    /// Validates that <paramref name="absolutePath"/> exists as a directory, constructs a
    /// <see cref="DysonLocalWorkspaceFileSystem"/>, and initializes it with
    /// <see cref="DysonWorkspaceSubjects.LocalFs"/>.
    /// </summary>
    public static async Task<Result<IDysonWorkspaceFileSystem, string>> CreateLocalAsync(
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<IDysonWorkspaceFileSystem, string>.AsError($"Invalid path: {ex.Message}");
        }

        var exists = await DysonLocalWorkspaceFileSystem
            .RunIoAsync(() => Directory.Exists(fullPath), cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
            return Result<IDysonWorkspaceFileSystem, string>.AsError("Directory does not exist.");

        var fs = new DysonLocalWorkspaceFileSystem(fullPath);
        var init = await fs.InitializeAsync(DysonWorkspaceSubjects.LocalFs, cancellationToken)
            .ConfigureAwait(false);
        if (init.IsError)
            return Result<IDysonWorkspaceFileSystem, string>.AsError(init.Error);

        return Result<IDysonWorkspaceFileSystem, string>.AsValue(fs);
    }
}

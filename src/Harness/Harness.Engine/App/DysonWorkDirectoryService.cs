namespace DysonHarness;

/// <summary>
/// Work-directory git origin refresh and provider mapping. Persistence stays on
/// <see cref="IDysonWorkDirectoryRepository"/>; this type only classifies and writes
/// metadata on activation.
/// </summary>
public sealed class DysonWorkDirectoryService(IDysonWorkDirectoryRepository workDirectories)
{
    private readonly IDysonWorkDirectoryRepository _workDirectories =
        workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));

    /// <summary>
    /// Detects <c>origin</c> for the registered path and persists origin + provider.
    /// Detection failure (no git, timeout, no origin) writes null/null so a removed
    /// remote does not stay classified.
    /// </summary>
    public async Task<VoidResult<string>> RefreshGitOriginAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        var get = await _workDirectories.GetAsync(workDirectoryId, cancellationToken)
            .ConfigureAwait(false);
        if (get.IsError)
            return VoidResult<string>.AsError(get.Error);

        var origin = DysonGitInfo.TryGetOrigin(get.Value.AbsolutePath);
        string? gitOrigin = null;
        string? gitProvider = null;
        if (origin.IsSuccess)
        {
            gitOrigin = origin.Value;
            gitProvider = DysonGitInfo.ToStoredSlug(DysonGitInfo.ClassifyProvider(origin.Value));
        }

        return await _workDirectories
            .UpdateGitMetadataAsync(workDirectoryId, gitOrigin, gitProvider, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Maps stored <see cref="DysonWorkDirectoryEntity.GitProvider"/>; empty stored
    /// classifies from <see cref="DysonWorkDirectoryEntity.GitOrigin"/>; else
    /// <see cref="DysonGitProvider.None"/>.
    /// </summary>
    public DysonGitProvider GetGitProvider(DysonWorkDirectoryEntity workDirectory)
    {
        ArgumentNullException.ThrowIfNull(workDirectory);
        return GetGitProvider(workDirectory.GitProvider, workDirectory.GitOrigin);
    }

    /// <summary>
    /// Maps a stored provider slug; empty stored classifies from
    /// <paramref name="origin"/>; else <see cref="DysonGitProvider.None"/>.
    /// </summary>
    public DysonGitProvider GetGitProvider(string? storedProvider, string? origin = null)
    {
        var mapped = DysonGitInfo.FromStoredSlug(storedProvider);
        if (mapped != DysonGitProvider.None)
            return mapped;

        return string.IsNullOrWhiteSpace(storedProvider)
            ? DysonGitInfo.ClassifyProvider(origin)
            : DysonGitProvider.None;
    }
}

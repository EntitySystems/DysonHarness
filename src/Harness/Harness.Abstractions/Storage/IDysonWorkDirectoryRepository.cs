namespace DysonHarness;

/// <summary>
/// Subject-owned work directory registrations.
/// Visibility: current subject only; cross-subject get-by-id → error.
/// </summary>
public interface IDysonWorkDirectoryRepository
{
    Task<Result<Guid, string>> CreateAsync(
        string absolutePath,
        string? name = null,
        CancellationToken cancellationToken = default);

    Task<Result<DysonWorkDirectoryEntity, string>> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> TouchOpenedAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists classified git origin metadata. Both values may be null to clear a stale remote.
    /// </summary>
    Task<VoidResult<string>> UpdateGitMetadataAsync(
        Guid id,
        string? gitOrigin,
        string? gitProvider,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the work directory registration. Blocked when any sessions still reference it.
    /// Does not delete the folder on disk.
    /// </summary>
    Task<VoidResult<string>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

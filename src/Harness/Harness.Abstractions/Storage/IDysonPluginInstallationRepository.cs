namespace DysonHarness;

/// <summary>
/// Subject-owned durable plugin installation records. Passing a work-directory id to
/// <see cref="ListAsync"/> returns global records plus that project's records; null returns globals only.
/// </summary>
public interface IDysonPluginInstallationRepository
{
    Task<Result<Guid, string>> UpsertAsync(
        DysonPluginInstallationEntity installation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces one already-owned installation record. Unlike <see cref="UpsertAsync"/>, this
    /// cannot create a record or select a different record by its natural scope key.
    /// </summary>
    Task<VoidResult<string>> ReplaceAsync(
        Guid id,
        DysonPluginInstallationEntity installation,
        CancellationToken cancellationToken = default);

    Task<Result<DysonPluginInstallationEntity, string>> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonPluginInstallationEntity>, string>> ListAsync(
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> SetEnabledAsync(
        Guid id,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

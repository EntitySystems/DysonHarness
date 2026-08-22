namespace DysonHarness;

/// <summary>
/// Subject-owned append-only usage rows. Visibility: current subject only.
/// </summary>
public interface IDysonUsageAnalyticsRepository
{
    Task<VoidResult<string>> AppendAsync(
        DysonUsageRequestEntity row,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists usage for the current subject, newest first.
    /// Optional work-directory name and UTC window filter later workdir analytics.
    /// </summary>
    Task<Result<IReadOnlyList<DysonUsageRequestEntity>, string>> ListAsync(
        string? workDirectoryName = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Usage-panel recap: all rows whose <see cref="DysonUsageRequestEntity.RootSessionId"/>
    /// matches (root + descendant subagents).
    /// </summary>
    Task<Result<IReadOnlyList<DysonUsageRequestEntity>, string>> ListByRootSessionAsync(
        Guid rootSessionId,
        CancellationToken cancellationToken = default);
}

namespace DysonHarness;

/// <summary>
/// Change Stream rows (<c>file_changes</c>), current subject only. Rows are keyed by the root session.
/// </summary>
public interface IDysonFileChangeRepository
{
    /// <summary>
    /// Inserts a row and returns the database-assigned id. <see cref="DysonFileChangeEntity.Id"/>,
    /// <see cref="DysonFileChangeEntity.SubjectId"/> and <see cref="DysonFileChangeEntity.CreatedUtc"/> are set
    /// by the repository. Prunes old rows of the same root past the retention cap.
    /// </summary>
    Task<Result<ulong, string>> AppendAsync(
        DysonFileChangeEntity row,
        CancellationToken cancellationToken = default);

    /// <summary>Newest first. <paramref name="beforeId"/> (exclusive) pages to older rows.</summary>
    Task<Result<IReadOnlyList<DysonFileChangeEntity>, string>> ListAsync(
        Guid rootSessionId,
        ulong? beforeId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>One row by id, or null when missing or owned by another subject.</summary>
    Task<Result<DysonFileChangeEntity?, string>> GetAsync(
        ulong id,
        CancellationToken cancellationToken = default);
}

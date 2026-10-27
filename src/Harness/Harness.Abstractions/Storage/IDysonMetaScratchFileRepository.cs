namespace DysonHarness;

/// <summary>
/// Work-directory-scoped Meta scratch files (current subject only). Every lookup is scoped by
/// <c>workDirectoryId</c>; never trust the id alone. Hard delete only (no archive).
/// </summary>
public interface IDysonMetaScratchFileRepository
{
    /// <summary>Maximum body length in characters (<c>string.Length</c>), 128K.</summary>
    const int MaxContentChars = 131_072;

    /// <summary>
    /// Metadata only (<see cref="DysonMetaScratchFile.Content"/> null). Order: non-null <c>SortIndex</c>
    /// ascending (then id), then null <c>SortIndex</c> by <c>UpdatedUtc</c> descending (then id descending).
    /// </summary>
    Task<Result<IReadOnlyList<DysonMetaScratchFile>, string>> ListAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>Full row including body.</summary>
    Task<Result<DysonMetaScratchFile, string>> GetAsync(
        long id,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a row (null <c>SortIndex</c>) and returns the database-assigned id.</summary>
    Task<Result<long, string>> CreateAsync(
        Guid workDirectoryId,
        string title,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Patch semantics (null = unchanged). Bumps <c>UpdatedUtc</c>. Both null ⇒ <c>Nothing to update.</c></summary>
    Task<VoidResult<string>> UpdateAsync(
        long id,
        Guid workDirectoryId,
        string? title,
        string? content,
        CancellationToken cancellationToken = default);

    /// <summary>Hard delete.</summary>
    Task<VoidResult<string>> DeleteAsync(
        long id,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the row to <paramref name="toPosition"/> (clamped) in list order and renumbers every row of the
    /// work directory densely <c>0..n-1</c>. Does not touch <c>UpdatedUtc</c>.
    /// </summary>
    Task<VoidResult<string>> MoveAsync(
        long id,
        Guid workDirectoryId,
        int toPosition,
        CancellationToken cancellationToken = default);
}

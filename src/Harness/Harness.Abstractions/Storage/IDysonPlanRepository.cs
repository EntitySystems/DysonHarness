namespace DysonHarness;

/// <summary>
/// Work-directory-scoped plans (current subject only).
/// Sequential <c>planId</c> values are guessable — every lookup is scoped by
/// <c>workDirectoryId</c>; never trust the id alone.
/// </summary>
public interface IDysonPlanRepository
{
    /// <summary>
    /// Metadata only (no <see cref="DysonPlan.Markdown"/>), newest <see cref="DysonPlan.UpdatedUtc"/> first.
    /// </summary>
    Task<Result<IReadOnlyList<DysonPlan>, string>> ListAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>Full row including body. Fails when <paramref name="planId"/> is not in <paramref name="workDirectoryId"/>.</summary>
    Task<Result<DysonPlan, string>> GetAsync(
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a row and returns the database-assigned <c>planId</c> (after save).
    /// Enforces: MetaPlan ⇒ non-empty Markdown and null path; ClassicPlan ⇒ the reverse.
    /// </summary>
    Task<Result<long, string>> CreateAsync(
        Guid workDirectoryId,
        DysonPlanKind kind,
        string title,
        string? markdown,
        string? planRelativePath,
        DysonPlanStatus status = DysonPlanStatus.Draft,
        string? note = null,
        Guid? buildAgentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Patch semantics (null argument = leave unchanged). Bumps <c>UpdatedUtc</c>.</summary>
    Task<VoidResult<string>> UpdateAsync(
        long planId,
        Guid workDirectoryId,
        string? title = null,
        string? markdown = null,
        DysonPlanStatus? status = null,
        string? note = null,
        Guid? buildAgentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the row. Fails when <paramref name="planId"/> is not in <paramref name="workDirectoryId"/>.</summary>
    Task<VoidResult<string>> DeleteAsync(
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);
}

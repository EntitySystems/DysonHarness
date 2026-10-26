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
    /// Archived rows (<see cref="DysonPlan.ArchivedUtc"/> set) are excluded unless <paramref name="includeArchived"/>.
    /// </summary>
    Task<Result<IReadOnlyList<DysonPlan>, string>> ListAsync(
        Guid workDirectoryId,
        bool includeArchived = false,
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
        CancellationToken cancellationToken = default,
        DateTime? createdUtc = null);

    /// <summary>
    /// Patch semantics (null argument = leave unchanged). Bumps <c>UpdatedUtc</c>.
    /// Fails with <c>Plan '{id}' is archived.</c> when the row is archived.
    /// </summary>
    Task<VoidResult<string>> UpdateAsync(
        long planId,
        Guid workDirectoryId,
        string? title = null,
        string? markdown = null,
        DysonPlanStatus? status = null,
        string? note = null,
        Guid? buildAgentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets <c>ArchivedUtc</c> and bumps <c>UpdatedUtc</c>; status, note, and build agent are unchanged.
    /// Refuses a <see cref="DysonPlanStatus.Building"/> plan. Already archived: success, no write.
    /// </summary>
    Task<VoidResult<string>> ArchiveAsync(
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears <c>ArchivedUtc</c> and bumps <c>UpdatedUtc</c>; status unchanged. Not archived: success, no write.
    /// </summary>
    Task<VoidResult<string>> RestoreAsync(
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard delete. Not the <c>DeletePlan</c> tool path (that archives). Fails when <paramref name="planId"/> is not in <paramref name="workDirectoryId"/>.
    /// </summary>
    Task<VoidResult<string>> DeleteAsync(
        long planId,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default);
}

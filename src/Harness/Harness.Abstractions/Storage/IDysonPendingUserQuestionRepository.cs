namespace DysonHarness;

/// <summary>
/// Pending (unanswered) Meta questions, current subject only. Every call is scoped by <c>sessionId</c>;
/// a session that does not belong to the current subject is an error. Hard delete only.
/// </summary>
public interface IDysonPendingUserQuestionRepository
{
    /// <summary>
    /// Inserts a row and returns the database-assigned id. <see cref="DysonPendingUserQuestion.Id"/> and
    /// <see cref="DysonPendingUserQuestion.CreatedUtc"/> on <paramref name="question"/> are ignored.
    /// </summary>
    Task<Result<ulong, string>> CreateAsync(
        DysonPendingUserQuestion question,
        CancellationToken cancellationToken = default);

    /// <summary>Oldest first (<c>CreatedUtc</c>, then id).</summary>
    Task<Result<IReadOnlyList<DysonPendingUserQuestion>, string>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Row count for the header pill.</summary>
    Task<Result<int, string>> CountAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Hard delete. A missing row is success (dismiss and answer can race).</summary>
    Task<VoidResult<string>> DeleteAsync(
        ulong id,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes by card id (the inline card only knows the Guid). A missing row is success.</summary>
    Task<VoidResult<string>> DeleteByQuestionGuidAsync(
        Guid questionGuid,
        Guid sessionId,
        CancellationToken cancellationToken = default);
}

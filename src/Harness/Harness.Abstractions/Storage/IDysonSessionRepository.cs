namespace DysonHarness;

/// <summary>
/// Subject-owned sessions, turns, logs, and todos.
/// Visibility: filter/write current <see cref="IDysonSubjectContext.SubjectId"/> only;
/// cross-subject get-by-id → error.
/// </summary>
public interface IDysonSessionRepository
{
    Task<Result<Guid, string>> CreateSessionAsync(
        DysonSessionCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> UpdateSessionMetaAsync(
        DysonSessionMetaUpdate update,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> UpsertTurnAsync(
        DysonTurnEntity turn,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> AppendLogAsync(
        DysonSessionLogEntry entry,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(
        Guid? workDirectoryId = null,
        bool rootsOnly = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Direct child sessions for <paramref name="parentSessionId"/>, ordered by
    /// <see cref="DysonSessionSummary.RuntimeId"/>.
    /// </summary>
    Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListChildSessionsAsync(
        Guid parentSessionId,
        CancellationToken cancellationToken = default);

    Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Current-subject sessions with <see cref="DysonSessionStatus.Active"/> that still have
    /// at least one turn whose <see cref="DysonTurnEntity.CompletedUtc"/> is null.
    /// Cross-subject rows are never returned.
    /// </summary>
    Task<Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>>
        ListActiveSessionsWithUnfinishedTurnsAsync(
            CancellationToken cancellationToken = default);

    /// <summary>
    /// Current-subject sessions with <see cref="DysonSessionStatus.Active"/> and a non-null
    /// <see cref="DysonSessionEntity.ParentSessionId"/>. Cross-subject rows are never returned.
    /// </summary>
    Task<Result<IReadOnlyList<DysonSessionSummary>, string>>
        ListActiveDescendantSessionsAsync(
            CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a session and its descendant subagent sessions. Turns, logs, and todos cascade.
    /// </summary>
    Task<VoidResult<string>> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<Result<DysonSessionTodo, string>> CreateTodoAsync(
        DysonSessionTodoCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(
        DysonSessionTodoUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> DeleteTodoAsync(
        Guid sessionId,
        string taskCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the session's todo list (delete all, then insert <paramref name="items"/> in order).
    /// </summary>
    Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(
        Guid sessionId,
        IReadOnlyList<DysonSessionTodoReplaceItem> items,
        CancellationToken cancellationToken = default);
}

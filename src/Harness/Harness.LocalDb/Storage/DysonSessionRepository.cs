using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonSessionRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonSessionRepository
{
    private static readonly JsonSerializerOptions TodoJsonOptions = new();

    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<Result<Guid, string>> CreateSessionAsync(
        DysonSessionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => CreateSessionCoreAsync(db, subjectId, request, ct), cancellationToken);
    }

    public Task<VoidResult<string>> UpdateSessionMetaAsync(
        DysonSessionMetaUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => UpdateSessionMetaCoreAsync(db, subjectId, update, ct), cancellationToken);
    }

    public async Task<VoidResult<string>> UpsertTurnAsync(
        DysonTurnEntity turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var subjectId = _subjectContext.SubjectId;
        const int maxAttempts = 5;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _accessor.RunAsync(
                        (db, ct) => UpsertTurnCoreAsync(db, subjectId, turn, ct),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts - 1 && DysonDbAccessor.IsContention(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new VoidResult<string>($"Failed to upsert turn: {ex.Message}");
            }
        }
    }

    public Task<VoidResult<string>> AppendLogAsync(
        DysonSessionLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => AppendLogCoreAsync(db, subjectId, entry, ct), cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(
        Guid? workDirectoryId = null,
        bool rootsOnly = true,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListSessionsCoreAsync(db, subjectId, workDirectoryId, rootsOnly, ct),
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListChildSessionsAsync(
        Guid parentSessionId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListChildSessionsCoreAsync(db, subjectId, parentSessionId, ct),
            cancellationToken);
    }

    public Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => GetFullSessionCoreAsync(db, subjectId, sessionId, ct), cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>>
        ListActiveSessionsWithUnfinishedTurnsAsync(CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListActiveSessionsWithUnfinishedTurnsCoreAsync(db, subjectId, ct),
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSessionSummary>, string>>
        ListActiveDescendantSessionsAsync(CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListActiveDescendantSessionsCoreAsync(db, subjectId, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => DeleteSessionCoreAsync(db, subjectId, sessionId, ct), cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => ListTodosCoreAsync(db, subjectId, sessionId, ct), cancellationToken);
    }

    public Task<Result<DysonSessionTodo, string>> CreateTodoAsync(
        DysonSessionTodoCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => CreateTodoCoreAsync(db, subjectId, request, ct), cancellationToken);
    }

    public Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(
        DysonSessionTodoUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => UpdateTodoCoreAsync(db, subjectId, request, ct), cancellationToken);
    }

    public Task<VoidResult<string>> DeleteTodoAsync(
        Guid sessionId,
        string taskCode,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => DeleteTodoCoreAsync(db, subjectId, sessionId, taskCode, ct),
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(
        Guid sessionId,
        IReadOnlyList<DysonSessionTodoReplaceItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ReplaceTodosCoreAsync(db, subjectId, sessionId, items, ct),
            cancellationToken);
    }

    private static async Task<Result<Guid, string>> CreateSessionCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonSessionCreateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.WorkDirectoryId is Guid wdId)
            {
                var wdOk = await db.WorkDirectories
                    .AsNoTracking()
                    .AnyAsync(w => w.Id == wdId && w.SubjectId == subjectId, cancellationToken)
                    .ConfigureAwait(false);
                if (!wdOk)
                    return Result<Guid, string>.AsError($"Work directory '{wdId}' not found.");
            }

            if (request.ParentSessionId is Guid parentId)
            {
                var parentOk = await db.Sessions
                    .AsNoTracking()
                    .AnyAsync(s => s.Id == parentId && s.SubjectId == subjectId, cancellationToken)
                    .ConfigureAwait(false);
                if (!parentOk)
                    return Result<Guid, string>.AsError($"Parent session '{parentId}' not found.");
            }

            if (request.ModelSlugId is Guid slugId)
            {
                var slugOk = await IsVisibleSlugAsync(db, subjectId, slugId, cancellationToken)
                    .ConfigureAwait(false);
                if (!slugOk)
                    return Result<Guid, string>.AsError($"Model slug '{slugId}' not found.");
            }

            var now = DateTime.UtcNow;
            var entity = new DysonSessionEntity
            {
                Id = Guid.NewGuid(),
                SubjectId = subjectId,
                RuntimeId = request.RuntimeId,
                ParentSessionId = request.ParentSessionId,
                AgentMode = request.AgentMode,
                ModelSlugId = request.ModelSlugId,
                WorkDirectoryId = request.WorkDirectoryId,
                ReasoningEffort = request.ReasoningEffort,
                MaxTargetContextTokens = request.MaxTargetContextTokens,
                McpAccessMode = request.McpAccessMode,
                Status = request.Status,
                Title = request.Title,
                SystemPromptSnapshot = request.SystemPromptSnapshot,
                WorktreeEnabled = request.WorktreeEnabled,
                WorktreeAbsolutePath = request.WorktreeAbsolutePath,
                WorktreeBranch = request.WorktreeBranch,
                CreatedUtc = now,
                UpdatedUtc = now,
                LastActivityUtc = now,
            };

            db.Sessions.Add(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(entity.Id);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<Guid, string>.AsError($"Failed to create session: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> UpdateSessionMetaCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonSessionMetaUpdate update,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == update.SessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return new VoidResult<string>($"Session '{update.SessionId}' not found.");

            if (update.Status is not null)
                entity.Status = update.Status.Value;

            if (update.Title is not null)
                entity.Title = update.Title;

            if (update.ClearModelSlug)
                entity.ModelSlugId = null;
            else if (update.ModelSlugId is not null)
            {
                var slugOk = await IsVisibleSlugAsync(db, subjectId, update.ModelSlugId.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (!slugOk)
                    return new VoidResult<string>($"Model slug '{update.ModelSlugId}' not found.");
                entity.ModelSlugId = update.ModelSlugId;
            }

            if (update.UpdateReasoningEffort)
                entity.ReasoningEffort = update.ReasoningEffort;

            if (update.UpdateMaxTargetContextTokens)
                entity.MaxTargetContextTokens = update.MaxTargetContextTokens;

            if (update.AgentMode is not null)
                entity.AgentMode = update.AgentMode;

            if (update.SystemPromptSnapshot is not null)
                entity.SystemPromptSnapshot = update.SystemPromptSnapshot;

            if (update.UpdateWorktreeEnabled)
                entity.WorktreeEnabled = update.WorktreeEnabled;

            if (update.UpdateWorktreeLocation)
            {
                entity.WorktreeAbsolutePath = update.WorktreeAbsolutePath;
                entity.WorktreeBranch = update.WorktreeBranch;
            }

            var now = DateTime.UtcNow;
            entity.UpdatedUtc = now;
            entity.LastActivityUtc = now;

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to update session meta: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> UpsertTurnCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonTurnEntity turn,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == turn.SessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
                return new VoidResult<string>($"Session '{turn.SessionId}' not found.");

            if (turn.Id == Guid.Empty)
                turn.Id = Guid.NewGuid();

            var existing = await db.Turns
                .FirstOrDefaultAsync(t => t.Id == turn.Id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                if (turn.CreatedUtc == default)
                    turn.CreatedUtc = DateTime.UtcNow;

                db.Turns.Add(turn);
            }
            else
            {
                existing.SessionId = turn.SessionId;
                existing.Sequence = turn.Sequence;
                existing.Kind = turn.Kind;
                existing.AgentTitle = turn.AgentTitle;
                existing.PlanRelativePath = turn.PlanRelativePath;
                existing.Instruction = turn.Instruction;
                existing.AssistantText = turn.AssistantText;
                existing.ReasoningText = turn.ReasoningText;
                existing.ReasoningLogJson = turn.ReasoningLogJson;
                existing.SkillsUsedJson = turn.SkillsUsedJson;
                existing.UserImagesJson = turn.UserImagesJson;
                existing.ToolStateJson = turn.ToolStateJson;
                existing.ToolHistoryOptimized = turn.ToolHistoryOptimized;
                existing.CompactToolHistory = turn.CompactToolHistory;
                existing.ContextSummary = turn.ContextSummary;
                existing.IsExcludedFromContext = turn.IsExcludedFromContext;
                existing.InterruptionReason = turn.InterruptionReason;
                existing.CompletedUtc = turn.CompletedUtc;
            }

            var now = DateTime.UtcNow;
            session.UpdatedUtc = now;
            session.LastActivityUtc = now;

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsContention(ex))
        {
            return new VoidResult<string>($"Failed to upsert turn: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> AppendLogCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonSessionLogEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == entry.SessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
                return new VoidResult<string>($"Session '{entry.SessionId}' not found.");

            if (entry.Id == Guid.Empty)
                entry.Id = Guid.NewGuid();

            if (entry.TimestampUtc == default)
                entry.TimestampUtc = DateTime.UtcNow;

            if (entry.Sequence <= 0)
                entry.Sequence = await NextLogSequenceAsync(db, entry.SessionId, cancellationToken)
                    .ConfigureAwait(false);

            db.SessionLogs.Add(entry);

            var now = DateTime.UtcNow;
            session.UpdatedUtc = now;
            session.LastActivityUtc = now;

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to append session log: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid? workDirectoryId,
        bool rootsOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = db.Sessions.AsNoTracking().Where(s => s.SubjectId == subjectId);
            if (rootsOnly)
                query = query.Where(s => s.ParentSessionId == null);

            if (workDirectoryId is Guid wd)
                query = query.Where(s => s.WorkDirectoryId == wd);

            var list = await query
                .OrderByDescending(s => s.LastActivityUtc)
                .Select(s => new DysonSessionSummary
                {
                    Id = s.Id,
                    RuntimeId = s.RuntimeId,
                    ParentSessionId = s.ParentSessionId,
                    AgentMode = s.AgentMode,
                    Status = s.Status,
                    Title = s.Title,
                    ModelSlugId = s.ModelSlugId,
                    WorkDirectoryId = s.WorkDirectoryId,
                    CreatedUtc = s.CreatedUtc,
                    LastActivityUtc = s.LastActivityUtc,
                    WorktreeEnabled = s.WorktreeEnabled,
                    HasWorktree = s.WorktreeAbsolutePath != null && s.WorktreeAbsolutePath != "",
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsError(
                $"Failed to list sessions: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListChildSessionsCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid parentSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var list = await db.Sessions
                .AsNoTracking()
                .Where(s => s.SubjectId == subjectId && s.ParentSessionId == parentSessionId)
                .OrderBy(s => s.RuntimeId)
                .Select(s => new DysonSessionSummary
                {
                    Id = s.Id,
                    RuntimeId = s.RuntimeId,
                    ParentSessionId = s.ParentSessionId,
                    AgentMode = s.AgentMode,
                    Status = s.Status,
                    Title = s.Title,
                    ModelSlugId = s.ModelSlugId,
                    WorkDirectoryId = s.WorkDirectoryId,
                    CreatedUtc = s.CreatedUtc,
                    LastActivityUtc = s.LastActivityUtc,
                    WorktreeEnabled = s.WorktreeEnabled,
                    HasWorktree = s.WorktreeAbsolutePath != null && s.WorktreeAbsolutePath != "",
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsError(
                $"Failed to list child sessions: {ex.Message}");
        }
    }

    private static async Task<Result<DysonPersistedSession, string>> GetFullSessionCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await db.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
                return Result<DysonPersistedSession, string>.AsError($"Session '{sessionId}' not found.");

            var turns = await db.Turns
                .AsNoTracking()
                .Where(t => t.SessionId == sessionId)
                .OrderBy(t => t.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var logs = await db.SessionLogs
                .AsNoTracking()
                .Where(l => l.SessionId == sessionId)
                .OrderBy(l => l.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var todoRows = await db.SessionTodos
                .AsNoTracking()
                .Where(t => t.SessionId == sessionId)
                .OrderBy(t => t.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<DysonPersistedSession, string>.AsValue(new DysonPersistedSession
            {
                Session = session,
                Turns = turns,
                Logs = logs,
                Todos = todoRows.Select(ToRuntimeTodo).ToList(),
            });
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonPersistedSession, string>.AsError(
                $"Failed to load full session: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>>
        ListActiveSessionsWithUnfinishedTurnsCoreAsync(
            DysonDbContext db,
            string subjectId,
            CancellationToken cancellationToken)
    {
        try
        {
            var rows = await (
                    from session in db.Sessions.AsNoTracking()
                    join turn in db.Turns.AsNoTracking() on session.Id equals turn.SessionId
                    where session.SubjectId == subjectId
                        && session.Status == DysonSessionStatus.Active
                        && turn.CompletedUtc == null
                    orderby session.LastActivityUtc, turn.Sequence
                    select new
                    {
                        session.Id,
                        session.ParentSessionId,
                        session.RuntimeId,
                        session.Status,
                        session.AgentMode,
                        session.Title,
                        session.WorkDirectoryId,
                        session.LastActivityUtc,
                        TurnId = turn.Id,
                        turn.Sequence,
                        turn.Kind,
                        turn.CreatedUtc,
                        turn.InterruptionReason,
                    })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var list = rows
                .GroupBy(row => row.Id)
                .Select(group =>
                {
                    var first = group.First();
                    return new DysonSessionUnfinishedWorkSummary
                    {
                        SessionId = first.Id,
                        ParentSessionId = first.ParentSessionId,
                        RuntimeId = first.RuntimeId,
                        Status = first.Status,
                        AgentMode = first.AgentMode,
                        Title = first.Title,
                        WorkDirectoryId = first.WorkDirectoryId,
                        LastActivityUtc = first.LastActivityUtc,
                        UnfinishedTurns = group
                            .OrderBy(row => row.Sequence)
                            .Select(row => new DysonUnfinishedTurnSummary
                            {
                                TurnId = row.TurnId,
                                Sequence = row.Sequence,
                                Kind = row.Kind,
                                CreatedUtc = row.CreatedUtc,
                                InterruptionReason = row.InterruptionReason,
                            })
                            .ToList(),
                    };
                })
                .OrderBy(summary => summary.LastActivityUtc)
                .ToList();

            return Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>.AsError(
                $"Failed to list sessions with unfinished turns: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonSessionSummary>, string>>
        ListActiveDescendantSessionsCoreAsync(
            DysonDbContext db,
            string subjectId,
            CancellationToken cancellationToken)
    {
        try
        {
            var list = await db.Sessions
                .AsNoTracking()
                .Where(s => s.SubjectId == subjectId
                    && s.Status == DysonSessionStatus.Active
                    && s.ParentSessionId != null)
                .OrderBy(s => s.LastActivityUtc)
                .ThenBy(s => s.RuntimeId)
                .Select(s => new DysonSessionSummary
                {
                    Id = s.Id,
                    RuntimeId = s.RuntimeId,
                    ParentSessionId = s.ParentSessionId,
                    AgentMode = s.AgentMode,
                    Status = s.Status,
                    Title = s.Title,
                    ModelSlugId = s.ModelSlugId,
                    WorkDirectoryId = s.WorkDirectoryId,
                    CreatedUtc = s.CreatedUtc,
                    LastActivityUtc = s.LastActivityUtc,
                    WorktreeEnabled = s.WorktreeEnabled,
                    HasWorktree = s.WorktreeAbsolutePath != null && s.WorktreeAbsolutePath != "",
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsError(
                $"Failed to list active descendant sessions: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteSessionCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (root is null)
                return new VoidResult<string>($"Session '{sessionId}' not found.");

            if (!string.IsNullOrEmpty(root.WorktreeAbsolutePath))
            {
                return new VoidResult<string>(
                    "Merge or delete this session's worktree before deleting the session.");
            }

            var ordered = new List<Guid>();
            var pending = new Queue<Guid>();
            pending.Enqueue(sessionId);
            while (pending.Count > 0)
            {
                var id = pending.Dequeue();
                ordered.Add(id);
                var childIds = await db.Sessions
                    .Where(s => s.ParentSessionId == id && s.SubjectId == subjectId)
                    .Select(s => s.Id)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var childId in childIds)
                    pending.Enqueue(childId);
            }

            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                var entity = i == 0
                    ? root
                    : await db.Sessions
                        .FirstOrDefaultAsync(s => s.Id == ordered[i] && s.SubjectId == subjectId, cancellationToken)
                        .ConfigureAwait(false);
                if (entity is not null)
                    db.Sessions.Remove(entity);
            }

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to delete session: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var exists = await db.Sessions
                .AsNoTracking()
                .AnyAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (!exists)
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    $"Session '{sessionId}' not found.");

            var rows = await db.SessionTodos
                .AsNoTracking()
                .Where(t => t.SessionId == sessionId)
                .OrderBy(t => t.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsValue(
                rows.Select(ToRuntimeTodo).ToList());
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                $"Failed to list todos: {ex.Message}");
        }
    }

    private static async Task<Result<DysonSessionTodo, string>> CreateTodoCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonSessionTodoCreateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var taskCode = NormalizeTaskCode(request.TaskCode);
            if (taskCode is null)
                return Result<DysonSessionTodo, string>.AsError("TaskCode is required.");

            if (string.IsNullOrWhiteSpace(request.DisplayName))
                return Result<DysonSessionTodo, string>.AsError("DisplayName is required.");

            if (!Enum.IsDefined(request.Status))
                return Result<DysonSessionTodo, string>.AsError($"Invalid status '{request.Status}'.");

            var sessionExists = await db.Sessions
                .AnyAsync(s => s.Id == request.SessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (!sessionExists)
                return Result<DysonSessionTodo, string>.AsError($"Session '{request.SessionId}' not found.");

            var duplicate = await db.SessionTodos
                .AnyAsync(
                    t => t.SessionId == request.SessionId && t.TaskCode == taskCode,
                    cancellationToken)
                .ConfigureAwait(false);

            if (duplicate)
            {
                return Result<DysonSessionTodo, string>.AsError(
                    $"Todo TaskCode '{taskCode}' already exists on session '{request.SessionId}'.");
            }

            var now = DateTime.UtcNow;
            var sequence = await NextTodoSequenceAsync(db, request.SessionId, cancellationToken)
                .ConfigureAwait(false);

            var entity = new DysonSessionTodoEntity
            {
                Id = Guid.NewGuid(),
                SessionId = request.SessionId,
                TaskCode = taskCode,
                DisplayName = request.DisplayName.Trim(),
                Status = request.Status,
                CommentsJson = SerializeComments(request.Comments),
                Sequence = sequence,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            db.SessionTodos.Add(entity);
            await TouchSessionActivityAsync(db, subjectId, request.SessionId, now, cancellationToken)
                .ConfigureAwait(false);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<DysonSessionTodo, string>.AsValue(ToRuntimeTodo(entity));
        }
        catch (DbUpdateException ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonSessionTodo, string>.AsError(
                $"Failed to create todo (duplicate TaskCode?): {ex.Message}");
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonSessionTodo, string>.AsError($"Failed to create todo: {ex.Message}");
        }
    }

    private static async Task<Result<DysonSessionTodo, string>> UpdateTodoCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonSessionTodoUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var taskCode = NormalizeTaskCode(request.TaskCode);
            if (taskCode is null)
                return Result<DysonSessionTodo, string>.AsError("TaskCode is required.");

            if (request.Status is { } status && !Enum.IsDefined(status))
                return Result<DysonSessionTodo, string>.AsError($"Invalid status '{status}'.");

            var sessionExists = await db.Sessions
                .AnyAsync(s => s.Id == request.SessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            if (!sessionExists)
                return Result<DysonSessionTodo, string>.AsError($"Session '{request.SessionId}' not found.");

            var entity = await db.SessionTodos
                .FirstOrDefaultAsync(
                    t => t.SessionId == request.SessionId && t.TaskCode == taskCode,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
            {
                return Result<DysonSessionTodo, string>.AsError(
                    $"Todo '{taskCode}' not found on session '{request.SessionId}'.");
            }

            if (request.DisplayName is not null)
            {
                if (string.IsNullOrWhiteSpace(request.DisplayName))
                    return Result<DysonSessionTodo, string>.AsError("DisplayName cannot be empty.");

                entity.DisplayName = request.DisplayName.Trim();
            }

            if (request.Status is not null)
                entity.Status = request.Status.Value;

            if (request.Comments is not null)
                entity.CommentsJson = SerializeComments(request.Comments);

            if (request.AppendComment is not null)
            {
                var comments = DeserializeComments(entity.CommentsJson).ToList();
                comments.Add(request.AppendComment);
                entity.CommentsJson = SerializeComments(comments);
            }

            var now = DateTime.UtcNow;
            entity.UpdatedUtc = now;
            await TouchSessionActivityAsync(db, subjectId, request.SessionId, now, cancellationToken)
                .ConfigureAwait(false);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<DysonSessionTodo, string>.AsValue(ToRuntimeTodo(entity));
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonSessionTodo, string>.AsError($"Failed to update todo: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteTodoCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        string taskCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalized = NormalizeTaskCode(taskCode);
            if (normalized is null)
                return new VoidResult<string>("TaskCode is required.");

            var sessionExists = await db.Sessions
                .AnyAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            if (!sessionExists)
                return new VoidResult<string>($"Session '{sessionId}' not found.");

            var entity = await db.SessionTodos
                .FirstOrDefaultAsync(
                    t => t.SessionId == sessionId && t.TaskCode == normalized,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return new VoidResult<string>($"Todo '{normalized}' not found on session '{sessionId}'.");

            db.SessionTodos.Remove(entity);
            await TouchSessionActivityAsync(db, subjectId, sessionId, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to delete todo: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        IReadOnlyList<DysonSessionTodoReplaceItem> items,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionExists = await db.Sessions
                .AnyAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (!sessionExists)
            {
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    $"Session '{sessionId}' not found.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var code = NormalizeTaskCode(item.TaskCode);
                if (code is null)
                {
                    return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                        $"items[{i}].TaskCode is required.");
                }

                if (string.IsNullOrWhiteSpace(item.DisplayName))
                {
                    return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                        $"items[{i}].DisplayName is required.");
                }

                if (!Enum.IsDefined(item.Status))
                {
                    return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                        $"items[{i}].Status is invalid.");
                }

                if (!seen.Add(code))
                {
                    return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                        $"Duplicate TaskCode '{code}' in replace set.");
                }
            }

            var existing = await db.SessionTodos
                .Where(t => t.SessionId == sessionId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (existing.Count > 0)
                db.SessionTodos.RemoveRange(existing);

            var now = DateTime.UtcNow;
            var created = new List<DysonSessionTodoEntity>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var entity = new DysonSessionTodoEntity
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    TaskCode = NormalizeTaskCode(item.TaskCode)!,
                    DisplayName = item.DisplayName.Trim(),
                    Status = item.Status,
                    CommentsJson = SerializeComments(item.Comments),
                    Sequence = i + 1,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
                created.Add(entity);
                db.SessionTodos.Add(entity);
            }

            await TouchSessionActivityAsync(db, subjectId, sessionId, now, cancellationToken).ConfigureAwait(false);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsValue(
                created.Select(ToRuntimeTodo).ToList());
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                $"Failed to replace todos: {ex.Message}");
        }
    }

    private static async Task TouchSessionActivityAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var session = await db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
            return;

        session.UpdatedUtc = utcNow;
        session.LastActivityUtc = utcNow;
    }

    private static async Task<int> NextTodoSequenceAsync(
        DysonDbContext db,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var dbMax = await db.SessionTodos
            .Where(t => t.SessionId == sessionId)
            .Select(t => (int?)t.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var localMax = db.SessionTodos.Local
            .Where(t => t.SessionId == sessionId)
            .Select(t => (int?)t.Sequence)
            .DefaultIfEmpty()
            .Max();

        return Math.Max(dbMax ?? 0, localMax ?? 0) + 1;
    }

    private static async Task<long> NextLogSequenceAsync(
        DysonDbContext db,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var dbMax = await db.SessionLogs
            .Where(l => l.SessionId == sessionId)
            .Select(l => (long?)l.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var localMax = db.SessionLogs.Local
            .Where(l => l.SessionId == sessionId)
            .Select(l => (long?)l.Sequence)
            .DefaultIfEmpty()
            .Max();

        return Math.Max(dbMax ?? 0, localMax ?? 0) + 1;
    }

    private static Task<bool> IsVisibleSlugAsync(
        DysonDbContext db,
        string subjectId,
        Guid slugId,
        CancellationToken cancellationToken) =>
        db.ModelSlugs
            .AsNoTracking()
            .AnyAsync(
                s => s.Id == slugId
                    && (s.Provider!.SubjectId == subjectId || s.Provider.SubjectId == DysonSubjects.Shared),
                cancellationToken);

    private static string? NormalizeTaskCode(string? taskCode)
    {
        if (string.IsNullOrWhiteSpace(taskCode))
            return null;

        return taskCode.Trim();
    }

    private static string SerializeComments(IReadOnlyList<string>? comments)
    {
        comments ??= [];
        return JsonSerializer.Serialize(comments, TodoJsonOptions);
    }

    private static IReadOnlyList<string> DeserializeComments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, TodoJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DysonSessionTodo ToRuntimeTodo(DysonSessionTodoEntity entity) =>
        new()
        {
            Id = entity.Id,
            SessionId = entity.SessionId,
            TaskCode = entity.TaskCode,
            DisplayName = entity.DisplayName,
            Status = entity.Status,
            Comments = DeserializeComments(entity.CommentsJson),
            Sequence = entity.Sequence,
            CreatedUtc = entity.CreatedUtc,
            UpdatedUtc = entity.UpdatedUtc,
        };
}

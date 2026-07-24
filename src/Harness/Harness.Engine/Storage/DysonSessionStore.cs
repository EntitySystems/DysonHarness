using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonSessionCreateRequest
{
    public int RuntimeId { get; init; }
    public Guid? ParentSessionId { get; init; }
    public required string AgentMode { get; init; }
    public Guid? ModelSlugId { get; init; }
    public Guid? WorkDirectoryId { get; init; }
    public DysonMcpAccessMode McpAccessMode { get; init; } = DysonMcpAccessMode.FullAccess;
    public string? Title { get; init; }
    public required string SystemPromptSnapshot { get; init; }
    public DysonSessionStatus Status { get; init; } = DysonSessionStatus.Active;
}

public sealed class DysonSessionMetaUpdate
{
    public Guid SessionId { get; init; }
    public DysonSessionStatus? Status { get; init; }
    public string? Title { get; init; }
    public Guid? ModelSlugId { get; init; }
    public bool ClearModelSlug { get; init; }
}

public sealed class DysonSessionSummary
{
    public Guid Id { get; init; }
    public int RuntimeId { get; init; }
    public Guid? ParentSessionId { get; init; }
    public string AgentMode { get; init; } = "";
    public DysonSessionStatus Status { get; init; }
    public string? Title { get; init; }
    public Guid? ModelSlugId { get; init; }
    public Guid? WorkDirectoryId { get; init; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; init; }
    /// <summary>UTC.</summary>
    public DateTime LastActivityUtc { get; init; }
}

public sealed class DysonPersistedSession
{
    public required DysonSessionEntity Session { get; init; }
    public required IReadOnlyList<DysonTurnEntity> Turns { get; init; }
    public required IReadOnlyList<DysonSessionLogEntry> Logs { get; init; }
    public required IReadOnlyList<DysonSessionTodo> Todos { get; init; }
}

public sealed class DysonSessionTodoCreateRequest
{
    public Guid SessionId { get; init; }
    public required string TaskCode { get; init; }
    public required string DisplayName { get; init; }
    public DysonSessionTodoStatus Status { get; init; } = DysonSessionTodoStatus.Pending;
    public IReadOnlyList<string>? Comments { get; init; }
}

public sealed class DysonSessionTodoUpdateRequest
{
    public Guid SessionId { get; init; }
    public required string TaskCode { get; init; }
    public string? DisplayName { get; init; }
    public DysonSessionTodoStatus? Status { get; init; }
    /// <summary>When set, replaces the full comments list.</summary>
    public IReadOnlyList<string>? Comments { get; init; }
    /// <summary>When set, appends one comment after any replace.</summary>
    public string? AppendComment { get; init; }
}

/// <summary>Seed/replace item (no SessionId; caller passes session separately).</summary>
public sealed class DysonSessionTodoReplaceItem
{
    public required string TaskCode { get; init; }
    public required string DisplayName { get; init; }
    public DysonSessionTodoStatus Status { get; init; } = DysonSessionTodoStatus.Pending;
    public IReadOnlyList<string>? Comments { get; init; }
}

public sealed class DysonSessionStore(DysonDbContext db)
{
    private static readonly JsonSerializerOptions TodoJsonOptions = new();

    private readonly DysonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    // ponytail: global lock serializes all session DbContext ops on the shared scoped context; upgrade path = IDbContextFactory per operation
    private readonly SemaphoreSlim _dbGate = new(1, 1);

    public Task<Result<Guid, string>> CreateSessionAsync(
        DysonSessionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunSerializedAsync(ct => CreateSessionCoreAsync(request, ct), cancellationToken);
    }

    public Task<VoidResult<string>> UpdateSessionMetaAsync(
        DysonSessionMetaUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return RunSerializedAsync(ct => UpdateSessionMetaCoreAsync(update, ct), cancellationToken);
    }

    public Task<VoidResult<string>> UpsertTurnAsync(
        DysonTurnEntity turn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return RunSerializedAsync(ct => UpsertTurnCoreAsync(turn, ct), cancellationToken);
    }

    public Task<VoidResult<string>> AppendLogAsync(
        DysonSessionLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RunSerializedAsync(ct => AppendLogCoreAsync(entry, ct), cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(
        Guid? workDirectoryId = null,
        bool rootsOnly = true,
        CancellationToken cancellationToken = default)
        => RunSerializedAsync(ct => ListSessionsCoreAsync(workDirectoryId, rootsOnly, ct), cancellationToken);

    public Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => RunSerializedAsync(ct => GetFullSessionCoreAsync(sessionId, ct), cancellationToken);

    /// <summary>
    /// Deletes a session and its descendant subagent sessions. Turns, logs, and todos cascade.
    /// </summary>
    public Task<VoidResult<string>> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => RunSerializedAsync(ct => DeleteSessionCoreAsync(sessionId, ct), cancellationToken);

    public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => RunSerializedAsync(ct => ListTodosCoreAsync(sessionId, ct), cancellationToken);

    public Task<Result<DysonSessionTodo, string>> CreateTodoAsync(
        DysonSessionTodoCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunSerializedAsync(ct => CreateTodoCoreAsync(request, ct), cancellationToken);
    }

    public Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(
        DysonSessionTodoUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunSerializedAsync(ct => UpdateTodoCoreAsync(request, ct), cancellationToken);
    }

    public Task<VoidResult<string>> DeleteTodoAsync(
        Guid sessionId,
        string taskCode,
        CancellationToken cancellationToken = default)
        => RunSerializedAsync(ct => DeleteTodoCoreAsync(sessionId, taskCode, ct), cancellationToken);

    /// <summary>
    /// Replaces the session's todo list (delete all, then insert <paramref name="items"/> in order).
    /// </summary>
    public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(
        Guid sessionId,
        IReadOnlyList<DysonSessionTodoReplaceItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return RunSerializedAsync(ct => ReplaceTodosCoreAsync(sessionId, items, ct), cancellationToken);
    }

    private async Task<T> RunSerializedAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task<Result<Guid, string>> CreateSessionCoreAsync(
        DysonSessionCreateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            var entity = new DysonSessionEntity
            {
                Id = Guid.NewGuid(),
                RuntimeId = request.RuntimeId,
                ParentSessionId = request.ParentSessionId,
                AgentMode = request.AgentMode,
                ModelSlugId = request.ModelSlugId,
                WorkDirectoryId = request.WorkDirectoryId,
                McpAccessMode = request.McpAccessMode,
                Status = request.Status,
                Title = request.Title,
                SystemPromptSnapshot = request.SystemPromptSnapshot,
                CreatedUtc = now,
                UpdatedUtc = now,
                LastActivityUtc = now,
            };

            _db.Sessions.Add(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(entity.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid, string>.AsError($"Failed to create session: {ex.Message}");
        }
    }

    private async Task<VoidResult<string>> UpdateSessionMetaCoreAsync(
        DysonSessionMetaUpdate update,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await _db.Sessions
                .FirstOrDefaultAsync(s => s.Id == update.SessionId, cancellationToken)
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
                entity.ModelSlugId = update.ModelSlugId;

            var now = DateTime.UtcNow;
            entity.UpdatedUtc = now;
            entity.LastActivityUtc = now;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to update session meta: {ex.Message}");
        }
    }

    private async Task<VoidResult<string>> UpsertTurnCoreAsync(
        DysonTurnEntity turn,
        CancellationToken cancellationToken)
    {
        try
        {
            if (turn.Id == Guid.Empty)
                turn.Id = Guid.NewGuid();

            var existing = await _db.Turns
                .FirstOrDefaultAsync(t => t.Id == turn.Id, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                if (turn.CreatedUtc == default)
                    turn.CreatedUtc = DateTime.UtcNow;

                _db.Turns.Add(turn);
            }
            else
            {
                existing.SessionId = turn.SessionId;
                existing.Sequence = turn.Sequence;
                existing.Kind = turn.Kind;
                existing.AgentTitle = turn.AgentTitle;
                existing.Instruction = turn.Instruction;
                existing.AssistantText = turn.AssistantText;
                existing.ToolStateJson = turn.ToolStateJson;
                existing.ToolHistoryOptimized = turn.ToolHistoryOptimized;
                existing.CompactToolHistory = turn.CompactToolHistory;
                existing.CompletedUtc = turn.CompletedUtc;
            }

            var session = await _db.Sessions
                .FirstOrDefaultAsync(s => s.Id == turn.SessionId, cancellationToken)
                .ConfigureAwait(false);

            if (session is not null)
            {
                var now = DateTime.UtcNow;
                session.UpdatedUtc = now;
                session.LastActivityUtc = now;
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to upsert turn: {ex.Message}");
        }
    }

    private async Task<VoidResult<string>> AppendLogCoreAsync(
        DysonSessionLogEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            if (entry.Id == Guid.Empty)
                entry.Id = Guid.NewGuid();

            if (entry.TimestampUtc == default)
                entry.TimestampUtc = DateTime.UtcNow;

            if (entry.Sequence <= 0)
                entry.Sequence = await NextLogSequenceAsync(entry.SessionId, cancellationToken)
                    .ConfigureAwait(false);

            _db.SessionLogs.Add(entry);

            var session = await _db.Sessions
                .FirstOrDefaultAsync(s => s.Id == entry.SessionId, cancellationToken)
                .ConfigureAwait(false);

            if (session is not null)
            {
                var now = DateTime.UtcNow;
                session.UpdatedUtc = now;
                session.LastActivityUtc = now;
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to append session log: {ex.Message}");
        }
    }

    private async Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsCoreAsync(
        Guid? workDirectoryId,
        bool rootsOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = _db.Sessions.AsNoTracking().AsQueryable();
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
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsValue(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DysonSessionSummary>, string>.AsError(
                $"Failed to list sessions: {ex.Message}");
        }
    }

    private async Task<Result<DysonPersistedSession, string>> GetFullSessionCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await _db.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
                return Result<DysonPersistedSession, string>.AsError($"Session '{sessionId}' not found.");

            var turns = await _db.Turns
                .AsNoTracking()
                .Where(t => t.SessionId == sessionId)
                .OrderBy(t => t.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var logs = await _db.SessionLogs
                .AsNoTracking()
                .Where(l => l.SessionId == sessionId)
                .OrderBy(l => l.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var todoRows = await _db.SessionTodos
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
        catch (Exception ex)
        {
            return Result<DysonPersistedSession, string>.AsError(
                $"Failed to load full session: {ex.Message}");
        }
    }

    private async Task<VoidResult<string>> DeleteSessionCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await _db.Sessions
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (root is null)
                return new VoidResult<string>($"Session '{sessionId}' not found.");

            // ParentSessionId is Restrict — remove descendants before the parent row.
            var ordered = new List<Guid>();
            var pending = new Queue<Guid>();
            pending.Enqueue(sessionId);
            while (pending.Count > 0)
            {
                var id = pending.Dequeue();
                ordered.Add(id);
                var childIds = await _db.Sessions
                    .Where(s => s.ParentSessionId == id)
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
                    : await _db.Sessions
                        .FirstOrDefaultAsync(s => s.Id == ordered[i], cancellationToken)
                        .ConfigureAwait(false);
                if (entity is not null)
                    _db.Sessions.Remove(entity);
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to delete session: {ex.Message}");
        }
    }

    private async Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var exists = await _db.Sessions
                .AsNoTracking()
                .AnyAsync(s => s.Id == sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (!exists)
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    $"Session '{sessionId}' not found.");

            var rows = await _db.SessionTodos
                .AsNoTracking()
                .Where(t => t.SessionId == sessionId)
                .OrderBy(t => t.Sequence)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsValue(
                rows.Select(ToRuntimeTodo).ToList());
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                $"Failed to list todos: {ex.Message}");
        }
    }

    private async Task<Result<DysonSessionTodo, string>> CreateTodoCoreAsync(
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

            var sessionExists = await _db.Sessions
                .AnyAsync(s => s.Id == request.SessionId, cancellationToken)
                .ConfigureAwait(false);

            if (!sessionExists)
                return Result<DysonSessionTodo, string>.AsError($"Session '{request.SessionId}' not found.");

            var duplicate = await _db.SessionTodos
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
            var sequence = await NextTodoSequenceAsync(request.SessionId, cancellationToken)
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

            _db.SessionTodos.Add(entity);
            await TouchSessionActivityAsync(request.SessionId, now, cancellationToken)
                .ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<DysonSessionTodo, string>.AsValue(ToRuntimeTodo(entity));
        }
        catch (DbUpdateException ex)
        {
            return Result<DysonSessionTodo, string>.AsError(
                $"Failed to create todo (duplicate TaskCode?): {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<DysonSessionTodo, string>.AsError($"Failed to create todo: {ex.Message}");
        }
    }

    private async Task<Result<DysonSessionTodo, string>> UpdateTodoCoreAsync(
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

            var entity = await _db.SessionTodos
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
            await TouchSessionActivityAsync(request.SessionId, now, cancellationToken)
                .ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<DysonSessionTodo, string>.AsValue(ToRuntimeTodo(entity));
        }
        catch (Exception ex)
        {
            return Result<DysonSessionTodo, string>.AsError($"Failed to update todo: {ex.Message}");
        }
    }

    private async Task<VoidResult<string>> DeleteTodoCoreAsync(
        Guid sessionId,
        string taskCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalized = NormalizeTaskCode(taskCode);
            if (normalized is null)
                return new VoidResult<string>("TaskCode is required.");

            var entity = await _db.SessionTodos
                .FirstOrDefaultAsync(
                    t => t.SessionId == sessionId && t.TaskCode == normalized,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return new VoidResult<string>($"Todo '{normalized}' not found on session '{sessionId}'.");

            _db.SessionTodos.Remove(entity);
            await TouchSessionActivityAsync(sessionId, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to delete todo: {ex.Message}");
        }
    }

    private async Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosCoreAsync(
        Guid sessionId,
        IReadOnlyList<DysonSessionTodoReplaceItem> items,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionExists = await _db.Sessions
                .AnyAsync(s => s.Id == sessionId, cancellationToken)
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

            var existing = await _db.SessionTodos
                .Where(t => t.SessionId == sessionId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (existing.Count > 0)
                _db.SessionTodos.RemoveRange(existing);

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
                _db.SessionTodos.Add(entity);
            }

            await TouchSessionActivityAsync(sessionId, now, cancellationToken).ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsValue(
                created.Select(ToRuntimeTodo).ToList());
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                $"Failed to replace todos: {ex.Message}");
        }
    }

    /// <summary>Caller must already hold <see cref="_dbGate"/>.</summary>
    private async Task TouchSessionActivityAsync(
        Guid sessionId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
            return;

        session.UpdatedUtc = utcNow;
        session.LastActivityUtc = utcNow;
    }

    /// <summary>Caller must already hold <see cref="_dbGate"/>.</summary>
    private async Task<int> NextTodoSequenceAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var dbMax = await _db.SessionTodos
            .Where(t => t.SessionId == sessionId)
            .Select(t => (int?)t.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var localMax = _db.SessionTodos.Local
            .Where(t => t.SessionId == sessionId)
            .Select(t => (int?)t.Sequence)
            .DefaultIfEmpty()
            .Max();

        return Math.Max(dbMax ?? 0, localMax ?? 0) + 1;
    }

    /// <summary>Caller must already hold <see cref="_dbGate"/>.</summary>
    private async Task<long> NextLogSequenceAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var dbMax = await _db.SessionLogs
            .Where(l => l.SessionId == sessionId)
            .Select(l => (long?)l.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var localMax = _db.SessionLogs.Local
            .Where(l => l.SessionId == sessionId)
            .Select(l => (long?)l.Sequence)
            .DefaultIfEmpty()
            .Max();

        return Math.Max(dbMax ?? 0, localMax ?? 0) + 1;
    }

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

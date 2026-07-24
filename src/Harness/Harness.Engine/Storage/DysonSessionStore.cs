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
}

public sealed class DysonSessionStore(DysonDbContext db)
{
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
    /// Deletes a session and its descendant subagent sessions. Turns and logs cascade.
    /// </summary>
    public Task<VoidResult<string>> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => RunSerializedAsync(ct => DeleteSessionCoreAsync(sessionId, ct), cancellationToken);

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

            return Result<DysonPersistedSession, string>.AsValue(new DysonPersistedSession
            {
                Session = session,
                Turns = turns,
                Logs = logs,
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
}

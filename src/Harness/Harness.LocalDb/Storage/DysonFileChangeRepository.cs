using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonFileChangeRepository : IDysonFileChangeRepository
{
    public const int DefaultRetainPerRoot = 2000;

    // ponytail: amortized prune, fixed cap (not a settings key). Upgrade path: a settings key.
    private const int PruneEvery = 50;

    private readonly DysonDbAccessor _accessor;
    private readonly IDysonSubjectContext _subjectContext;
    private readonly int _retainPerRoot;

    public DysonFileChangeRepository(DysonDbAccessor accessor, IDysonSubjectContext subjectContext)
        : this(accessor, subjectContext, DefaultRetainPerRoot)
    {
    }

    internal DysonFileChangeRepository(
        DysonDbAccessor accessor,
        IDysonSubjectContext subjectContext,
        int retainPerRoot)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _subjectContext = subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));
        _retainPerRoot = retainPerRoot > 0 ? retainPerRoot : DefaultRetainPerRoot;
    }

    public Task<Result<ulong, string>> AppendAsync(
        DysonFileChangeEntity row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.RootSessionId == Guid.Empty)
            return Task.FromResult(Result<ulong, string>.AsError("Root session id is required."));

        var subjectId = _subjectContext.SubjectId;
        var retain = _retainPerRoot;
        return _accessor.RunAsync(
            (db, ct) => AppendCoreAsync(db, subjectId, retain, row, ct),
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonFileChangeEntity>, string>> ListAsync(
        Guid rootSessionId,
        ulong? beforeId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (rootSessionId == Guid.Empty)
            return Task.FromResult(Result<IReadOnlyList<DysonFileChangeEntity>, string>.AsError(
                "Root session id is required."));

        var clamped = take < 1 ? 1 : take > 200 ? 200 : take;
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListCoreAsync(db, subjectId, rootSessionId, beforeId, clamped, ct),
            cancellationToken);
    }

    public Task<Result<DysonFileChangeEntity?, string>> GetAsync(
        ulong id,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => GetCoreAsync(db, subjectId, id, ct),
            cancellationToken);
    }

    private static async Task<Result<ulong, string>> AppendCoreAsync(
        DysonDbContext db,
        string subjectId,
        int retainPerRoot,
        DysonFileChangeEntity row,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await db.Sessions
                .AsNoTracking()
                .AnyAsync(s => s.Id == row.RootSessionId && s.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<ulong, string>.AsError($"Session '{row.RootSessionId}' not found.");

            // Leave Id unset so ValueGeneratedOnAdd assigns it.
            row.Id = 0;
            row.SubjectId = subjectId;
            row.CreatedUtc = DateTime.UtcNow;
            db.FileChanges.Add(row);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);

            var newId = row.Id;
            var stride = Math.Min(PruneEvery, retainPerRoot);
            if (stride > 0 && newId % (ulong)stride == 0)
                await PruneAsync(db, subjectId, row.RootSessionId, retainPerRoot, cancellationToken)
                    .ConfigureAwait(false);

            return Result<ulong, string>.AsValue(newId);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<ulong, string>.AsError($"Failed to append file change: {ex.Message}");
        }
    }

    private static async Task PruneAsync(
        DysonDbContext db,
        string subjectId,
        Guid rootSessionId,
        int retainPerRoot,
        CancellationToken cancellationToken)
    {
        // ponytail: EF client-evals ulong OrderBy. Fetch ids, cut in memory, delete the rest.
        var ids = await db.FileChanges
            .AsNoTracking()
            .Where(x => x.SubjectId == subjectId && x.RootSessionId == rootSessionId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ids.Count <= retainPerRoot)
            return;

        ids.Sort(static (a, b) => b.CompareTo(a));
        var cutoff = ids[retainPerRoot - 1];
        await db.FileChanges
            .Where(x => x.SubjectId == subjectId && x.RootSessionId == rootSessionId && x.Id < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<IReadOnlyList<DysonFileChangeEntity>, string>> ListCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid rootSessionId,
        ulong? beforeId,
        int take,
        CancellationToken cancellationToken)
    {
        try
        {
            // ponytail: EF client-evals ulong OrderBy. Sort the ids (≤ retention cap) in memory,
            // then load only the page's rows so the diff text of older rows never leaves SQLite.
            var query = db.FileChanges
                .AsNoTracking()
                .Where(x => x.SubjectId == subjectId && x.RootSessionId == rootSessionId);
            if (beforeId is { } before)
                query = query.Where(x => x.Id < before);

            var ids = await query.Select(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            ids.Sort(static (a, b) => b.CompareTo(a));
            if (ids.Count > take)
                ids.RemoveRange(take, ids.Count - take);

            var rows = await db.FileChanges
                .AsNoTracking()
                .Where(x => x.SubjectId == subjectId && ids.Contains(x.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            rows.Sort(static (a, b) => b.Id.CompareTo(a.Id));

            return Result<IReadOnlyList<DysonFileChangeEntity>, string>.AsValue(rows);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonFileChangeEntity>, string>.AsError(
                $"Failed to list file changes: {ex.Message}");
        }
    }

    private static async Task<Result<DysonFileChangeEntity?, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        ulong id,
        CancellationToken cancellationToken)
    {
        try
        {
            var row = await db.FileChanges
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            return Result<DysonFileChangeEntity?, string>.AsValue(row);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonFileChangeEntity?, string>.AsError($"Failed to load file change: {ex.Message}");
        }
    }
}

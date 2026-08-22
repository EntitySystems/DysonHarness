using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonUsageAnalyticsRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonUsageAnalyticsRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<VoidResult<string>> AppendAsync(
        DysonUsageRequestEntity row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => AppendCoreAsync(db, subjectId, row, ct), cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonUsageRequestEntity>, string>> ListAsync(
        string? workDirectoryName = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListCoreAsync(db, subjectId, workDirectoryName, fromUtc, toUtc, ct),
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonUsageRequestEntity>, string>> ListByRootSessionAsync(
        Guid rootSessionId,
        CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListByRootSessionCoreAsync(db, subjectId, rootSessionId, ct),
            cancellationToken);
    }

    private static async Task<VoidResult<string>> AppendCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonUsageRequestEntity row,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = new DysonUsageRequestEntity
            {
                Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id,
                SubjectId = subjectId,
                WorkDirectoryName = row.WorkDirectoryName ?? "",
                SessionId = row.SessionId,
                RootSessionId = row.RootSessionId,
                ModelSlug = row.ModelSlug ?? "",
                ModelDisplayAlias = string.IsNullOrWhiteSpace(row.ModelDisplayAlias)
                    ? row.ModelSlug ?? ""
                    : row.ModelDisplayAlias,
                ReasoningEffort = row.ReasoningEffort ?? "",
                OccurredUtc = row.OccurredUtc == default ? DateTime.UtcNow : row.OccurredUtc,
                InputTokens = ClampToken(row.InputTokens),
                CacheTokens = ClampToken(row.CacheTokens),
                WriteTokens = ClampToken(row.WriteTokens),
                CacheWriteTokens = ClampToken(row.CacheWriteTokens),
                InputTokensAfterCache = ClampToken(row.InputTokensAfterCache),
                WriteTokensAfterCache = ClampToken(row.WriteTokensAfterCache),
            };

            db.UsageRequests.Add(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to append usage request: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonUsageRequestEntity>, string>> ListCoreAsync(
        DysonDbContext db,
        string subjectId,
        string? workDirectoryName,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = db.UsageRequests.AsNoTracking().Where(r => r.SubjectId == subjectId);

            if (!string.IsNullOrWhiteSpace(workDirectoryName))
                query = query.Where(r => r.WorkDirectoryName == workDirectoryName);

            if (fromUtc is DateTime from)
                query = query.Where(r => r.OccurredUtc >= from);

            if (toUtc is DateTime to)
                query = query.Where(r => r.OccurredUtc <= to);

            var list = await query
                .OrderByDescending(r => r.OccurredUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonUsageRequestEntity>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonUsageRequestEntity>, string>.AsError(
                $"Failed to list usage requests: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonUsageRequestEntity>, string>> ListByRootSessionCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid rootSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var list = await db.UsageRequests
                .AsNoTracking()
                .Where(r => r.SubjectId == subjectId && r.RootSessionId == rootSessionId)
                .OrderByDescending(r => r.OccurredUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonUsageRequestEntity>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonUsageRequestEntity>, string>.AsError(
                $"Failed to list usage requests: {ex.Message}");
        }
    }

    private static int ClampToken(int value) => value < 0 ? 0 : value;
}

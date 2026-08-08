using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonPluginHookSecurityRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonPluginHookSecurityRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext = subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<VoidResult<string>> UpsertReviewAsync(DysonPluginHookReviewEntity review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, review.InstallationId, ct).ConfigureAwait(false))
                    return VoidResult<string>.AsError("Plugin installation not found for the current subject.");
                var entity = await db.PluginHookReviews.FirstOrDefaultAsync(x => x.SubjectId == subjectId &&
                    x.InstallationId == review.InstallationId && x.HookComponentId == review.HookComponentId &&
                    x.EventName == review.EventName, ct).ConfigureAwait(false);
                if (entity is null)
                {
                    entity = new DysonPluginHookReviewEntity { Id = Guid.NewGuid(), SubjectId = subjectId };
                    db.PluginHookReviews.Add(entity);
                }
                entity.InstallationId = review.InstallationId;
                entity.HookComponentId = review.HookComponentId;
                entity.EventName = review.EventName;
                entity.PermissionsJson = review.PermissionsJson;
                entity.FailureMode = review.FailureMode;
                entity.TimeoutMilliseconds = review.TimeoutMilliseconds;
                entity.MaxOutputBytes = review.MaxOutputBytes;
                entity.PackageChecksum = review.PackageChecksum;
                entity.ReviewedUtc = review.ReviewedUtc;
                entity.RevokedUtc = null;
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return VoidResult<string>.AsError("Failed to save plugin hook review.");
            }
        }, cancellationToken);
    }

    public Task<Result<DysonPluginHookReviewEntity?, string>> GetReviewAsync(Guid installationId, string hookComponentId, string eventName, CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, installationId, ct).ConfigureAwait(false))
                    return Result<DysonPluginHookReviewEntity?, string>.AsError("Plugin installation not found for the current subject.");
                var entity = await db.PluginHookReviews.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectId == subjectId &&
                    x.InstallationId == installationId && x.HookComponentId == hookComponentId && x.EventName == eventName, ct).ConfigureAwait(false);
                if (entity is not null)
                {
                    entity.ReviewedUtc = DateTime.SpecifyKind(entity.ReviewedUtc, DateTimeKind.Utc);
                    if (entity.RevokedUtc is DateTime revoked) entity.RevokedUtc = DateTime.SpecifyKind(revoked, DateTimeKind.Utc);
                }
                return Result<DysonPluginHookReviewEntity?, string>.AsValue(entity);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<DysonPluginHookReviewEntity?, string>.AsError("Failed to load plugin hook review.");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> RevokeReviewAsync(Guid installationId, string hookComponentId, string eventName, DateTime revokedUtc, CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                var entity = await db.PluginHookReviews.FirstOrDefaultAsync(x => x.SubjectId == subjectId &&
                    x.InstallationId == installationId && x.HookComponentId == hookComponentId && x.EventName == eventName, ct).ConfigureAwait(false);
                if (entity is null) return VoidResult<string>.AsError("Active plugin hook review not found.");
                entity.RevokedUtc = revokedUtc;
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return VoidResult<string>.AsError("Failed to revoke plugin hook review.");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> AppendAuditAsync(DysonPluginHookAuditEntity audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, audit.InstallationId, ct).ConfigureAwait(false))
                    return VoidResult<string>.AsError("Plugin installation not found for the current subject.");
                audit.Id = audit.Id == Guid.Empty ? Guid.NewGuid() : audit.Id;
                audit.SubjectId = subjectId;
                db.PluginHookAudits.Add(audit);
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return VoidResult<string>.AsError("Failed to append plugin hook audit record.");
            }
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonPluginHookAuditEntity>, string>> ListAuditAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, installationId, ct).ConfigureAwait(false))
                    return Result<IReadOnlyList<DysonPluginHookAuditEntity>, string>.AsError("Plugin installation not found for the current subject.");
                var rows = await db.PluginHookAudits.AsNoTracking().Where(x => x.SubjectId == subjectId && x.InstallationId == installationId)
                    .OrderBy(x => x.OccurredUtc).ToListAsync(ct).ConfigureAwait(false);
                foreach (var row in rows) row.OccurredUtc = DateTime.SpecifyKind(row.OccurredUtc, DateTimeKind.Utc);
                return Result<IReadOnlyList<DysonPluginHookAuditEntity>, string>.AsValue(rows);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlyList<DysonPluginHookAuditEntity>, string>.AsError("Failed to list plugin hook audit records.");
            }
        }, cancellationToken);
    }

    private static Task<bool> OwnsInstallationAsync(DysonDbContext db, string subjectId, Guid installationId, CancellationToken ct) =>
        db.PluginInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId && x.SubjectId == subjectId, ct);
}

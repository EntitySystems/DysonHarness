using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonPluginVariableValueRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonPluginVariableValueRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext = subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<VoidResult<string>> UpsertAsync(Guid installationId, string variableName, byte[] protectedValue, CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty) return Task.FromResult(VoidResult<string>.AsError("Plugin installation id is required."));
        if (string.IsNullOrWhiteSpace(variableName)) return Task.FromResult(VoidResult<string>.AsError("Plugin variable name is required."));
        if (protectedValue is null || protectedValue.Length == 0) return Task.FromResult(VoidResult<string>.AsError("Protected plugin variable value is required."));
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, installationId, ct).ConfigureAwait(false))
                    return VoidResult<string>.AsError("Plugin installation not found for the current subject.");
                var name = variableName.Trim();
                var entity = await db.PluginVariableValues.FirstOrDefaultAsync(
                    x => x.SubjectId == subjectId && x.InstallationId == installationId && x.VariableName == name, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (entity is null)
                {
                    entity = new DysonPluginVariableValueEntity
                    {
                        Id = Guid.NewGuid(), SubjectId = subjectId, InstallationId = installationId,
                        VariableName = name, CreatedUtc = now,
                    };
                    db.PluginVariableValues.Add(entity);
                }
                entity.ProtectedValue = [.. protectedValue];
                entity.UpdatedUtc = now;
                await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return VoidResult<string>.AsError("Failed to store protected plugin variable.");
            }
        }, cancellationToken);
    }

    public Task<Result<DysonPluginVariableValueEntity?, string>> GetAsync(Guid installationId, string variableName, CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty) return Task.FromResult(Result<DysonPluginVariableValueEntity?, string>.AsError("Plugin installation id is required."));
        if (string.IsNullOrWhiteSpace(variableName)) return Task.FromResult(Result<DysonPluginVariableValueEntity?, string>.AsError("Plugin variable name is required."));
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, installationId, ct).ConfigureAwait(false))
                    return Result<DysonPluginVariableValueEntity?, string>.AsError("Plugin installation not found for the current subject.");
                var entity = await db.PluginVariableValues.AsNoTracking().FirstOrDefaultAsync(
                    x => x.SubjectId == subjectId && x.InstallationId == installationId && x.VariableName == variableName.Trim(), ct).ConfigureAwait(false);
                if (entity is not null)
                {
                    entity.CreatedUtc = DateTime.SpecifyKind(entity.CreatedUtc, DateTimeKind.Utc);
                    entity.UpdatedUtc = DateTime.SpecifyKind(entity.UpdatedUtc, DateTimeKind.Utc);
                }
                return Result<DysonPluginVariableValueEntity?, string>.AsValue(entity);
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<DysonPluginVariableValueEntity?, string>.AsError("Failed to load protected plugin variable.");
            }
        }, cancellationToken);
    }

    public Task<Result<IReadOnlySet<string>, string>> ListNamesAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty) return Task.FromResult(Result<IReadOnlySet<string>, string>.AsError("Plugin installation id is required."));
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, installationId, ct).ConfigureAwait(false))
                    return Result<IReadOnlySet<string>, string>.AsError("Plugin installation not found for the current subject.");
                var names = await db.PluginVariableValues.AsNoTracking()
                    .Where(x => x.SubjectId == subjectId && x.InstallationId == installationId)
                    .Select(x => x.VariableName).ToListAsync(ct).ConfigureAwait(false);
                return Result<IReadOnlySet<string>, string>.AsValue(new HashSet<string>(names, StringComparer.Ordinal));
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return Result<IReadOnlySet<string>, string>.AsError("Failed to list protected plugin variables.");
            }
        }, cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(Guid installationId, string variableName, CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty) return Task.FromResult(VoidResult<string>.AsError("Plugin installation id is required."));
        if (string.IsNullOrWhiteSpace(variableName)) return Task.FromResult(VoidResult<string>.AsError("Plugin variable name is required."));
        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(async (db, ct) =>
        {
            try
            {
                if (!await OwnsInstallationAsync(db, subjectId, installationId, ct).ConfigureAwait(false))
                    return VoidResult<string>.AsError("Plugin installation not found for the current subject.");
                var entity = await db.PluginVariableValues.FirstOrDefaultAsync(
                    x => x.SubjectId == subjectId && x.InstallationId == installationId && x.VariableName == variableName.Trim(), ct).ConfigureAwait(false);
                if (entity is not null)
                {
                    db.PluginVariableValues.Remove(entity);
                    await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
                }
                return VoidResult<string>.Success;
            }
            catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
            {
                return VoidResult<string>.AsError("Failed to delete protected plugin variable.");
            }
        }, cancellationToken);
    }

    private static Task<bool> OwnsInstallationAsync(DysonDbContext db, string subjectId, Guid installationId, CancellationToken ct) =>
        db.PluginInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId && x.SubjectId == subjectId, ct);
}

using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonSubjectSettingsRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonSubjectSettingsRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<VoidResult<string>> EnsureSubjectAsync(CancellationToken cancellationToken = default)
    {
        var subjectId = _subjectContext.SubjectId;
        if (string.IsNullOrWhiteSpace(subjectId))
            return Task.FromResult(new VoidResult<string>("Subject id is required."));

        if (string.Equals(subjectId, DysonSubjects.Shared, StringComparison.Ordinal))
            return Task.FromResult(new VoidResult<string>("Cannot ensure the shared sentinel as a subject row."));

        return _accessor.RunAsync((db, ct) => EnsureSubjectCoreAsync(db, subjectId, ct), cancellationToken);
    }

    public Task<Result<string?, string>> GetSettingAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(Result<string?, string>.AsError("Setting key is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => GetCoreAsync(db, subjectId, key, ct), cancellationToken);
    }

    public Task<VoidResult<string>> SetSettingAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(new VoidResult<string>("Setting key is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => SetCoreAsync(db, subjectId, key, value, ct), cancellationToken);
    }

    private static async Task<VoidResult<string>> EnsureSubjectCoreAsync(
        DysonDbContext db,
        string subjectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var exists = await db.Subjects
                .AnyAsync(s => s.Id == subjectId, cancellationToken)
                .ConfigureAwait(false);

            if (!exists)
            {
                db.Subjects.Add(new DysonSubjectEntity
                {
                    Id = subjectId,
                    CreatedUtc = DateTime.UtcNow,
                });
                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            }

            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to ensure subject '{subjectId}': {ex.Message}");
        }
    }

    private static async Task<Result<string?, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.AppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SubjectId == subjectId && s.Key == key, cancellationToken)
                .ConfigureAwait(false);

            return Result<string?, string>.AsValue(entity?.Value);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<string?, string>.AsError($"Failed to read setting '{key}': {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> SetCoreAsync(
        DysonDbContext db,
        string subjectId,
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.AppSettings
                .FirstOrDefaultAsync(s => s.SubjectId == subjectId && s.Key == key, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (entity is not null)
                {
                    db.AppSettings.Remove(entity);
                    await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
                }

                return VoidResult<string>.Success;
            }

            if (entity is null)
            {
                db.AppSettings.Add(new DysonAppSettingEntity
                {
                    SubjectId = subjectId,
                    Key = key.Trim(),
                    Value = value.Trim(),
                });
            }
            else
            {
                entity.Value = value.Trim();
            }

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to write setting '{key}': {ex.Message}");
        }
    }
}

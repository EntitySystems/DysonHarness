using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonAppSettingsStore(DysonDbAccessor accessor)
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));

    public Task<Result<string?, string>> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(Result<string?, string>.AsError("Setting key is required."));

        return _accessor.RunAsync((db, ct) => GetCoreAsync(db, key, ct), cancellationToken);
    }

    /// <summary>Sets a value; null or whitespace deletes the row.</summary>
    public Task<VoidResult<string>> SetAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(new VoidResult<string>("Setting key is required."));

        return _accessor.RunAsync((db, ct) => SetCoreAsync(db, key, value, ct), cancellationToken);
    }

    private static async Task<Result<string?, string>> GetCoreAsync(
        DysonDbContext db,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.AppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
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
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
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

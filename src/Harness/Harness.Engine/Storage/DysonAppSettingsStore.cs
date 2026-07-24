using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonAppSettingsStore(DysonDbContext db)
{
    private readonly DysonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<Result<string?, string>> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Result<string?, string>.AsError("Setting key is required.");

        try
        {
            var entity = await _db.AppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
                .ConfigureAwait(false);

            return Result<string?, string>.AsValue(entity?.Value);
        }
        catch (Exception ex)
        {
            return Result<string?, string>.AsError($"Failed to read setting '{key}': {ex.Message}");
        }
    }

    /// <summary>Sets a value; null or whitespace deletes the row.</summary>
    public async Task<VoidResult<string>> SetAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new VoidResult<string>("Setting key is required.");

        try
        {
            var entity = await _db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (entity is not null)
                {
                    _db.AppSettings.Remove(entity);
                    await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                return VoidResult<string>.Success;
            }

            if (entity is null)
            {
                _db.AppSettings.Add(new DysonAppSettingEntity
                {
                    Key = key.Trim(),
                    Value = value.Trim(),
                });
            }
            else
            {
                entity.Value = value.Trim();
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to write setting '{key}': {ex.Message}");
        }
    }
}

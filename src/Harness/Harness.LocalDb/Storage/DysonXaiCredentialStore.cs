using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

/// <summary>
/// <see cref="IDysonXaiCredentialStore"/> over the existing <c>app_settings</c> table via the singleton
/// <see cref="DysonDbAccessor"/>. Plaintext by design (same stance as provider ApiKey / file_storage_s3).
/// Does not reuse SetSettingAsync: that is subject-filtered and trims / deletes on whitespace.
/// </summary>
public sealed class DysonXaiCredentialStore(DysonDbAccessor accessor) : IDysonXaiCredentialStore
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));

    public static string KeyFor(Guid credentialId) =>
        DysonAppSettingKeys.XaiGrokCredentialPrefix + credentialId.ToString("D");

    public Task<Result<string?, string>> GetAsync(Guid credentialId, CancellationToken cancellationToken = default) =>
        _accessor.RunAsync((db, ct) => GetCoreAsync(db, KeyFor(credentialId), ct), cancellationToken);

    public Task<VoidResult<string>> SaveAsync(
        Guid credentialId,
        string subjectId,
        string json,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
            return Task.FromResult(VoidResult<string>.AsError("Subject id is required."));
        if (string.IsNullOrEmpty(json))
            return Task.FromResult(VoidResult<string>.AsError("Credential JSON is required."));

        return _accessor.RunAsync((db, ct) => SaveCoreAsync(db, KeyFor(credentialId), subjectId, json, ct), cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(Guid credentialId, CancellationToken cancellationToken = default) =>
        _accessor.RunAsync((db, ct) => DeleteCoreAsync(db, KeyFor(credentialId), ct), cancellationToken);

    // ponytail: PK is (SubjectId, Key), so a key-only lookup scans app_settings. Ceiling = tens of rows;
    // upgrade = an index on Key (needs a migration, not worth it now).
    private static async Task<Result<string?, string>> GetCoreAsync(
        DysonDbContext db,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await db.AppSettings
                .AsNoTracking()
                .Where(s => s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<string?, string>.AsValue(value);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<string?, string>.AsError($"Failed to read xAI credential: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> SaveCoreAsync(
        DysonDbContext db,
        string key,
        string subjectId,
        string json,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
            {
                db.AppSettings.Add(new DysonAppSettingEntity
                {
                    SubjectId = subjectId,
                    Key = key,
                    Value = json,
                });
            }
            else
            {
                entity.Value = json;
            }

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to save xAI credential: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var entities = await db.AppSettings
                .Where(s => s.Key == key)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (entities.Count > 0)
            {
                db.AppSettings.RemoveRange(entities);
                await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            }

            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to delete xAI credential: {ex.Message}");
        }
    }
}

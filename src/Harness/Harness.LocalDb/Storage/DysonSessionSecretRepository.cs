using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonSessionSecretRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonSessionSecretRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<VoidResult<string>> UpsertAsync(
        Guid sessionId,
        string key,
        string description,
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Session id is required."));

        var trimmedKey = key?.Trim() ?? "";
        if (!DysonSessionSecretLimits.IsValidKey(trimmedKey))
            return Task.FromResult(VoidResult<string>.AsError(
                "Key must be 1..64 characters of letters, digits, dot, underscore, or hyphen."));

        var trimmedDescription = description?.Trim() ?? "";
        if (trimmedDescription.Length is 0 or > DysonSessionSecretLimits.MaxDescriptionLength)
            return Task.FromResult(VoidResult<string>.AsError("Description must be 1..200 characters."));

        if (string.IsNullOrEmpty(secret))
            return Task.FromResult(VoidResult<string>.AsError("Secret is required."));
        if (secret.Length > DysonSessionSecretLimits.MaxSecretLength)
            return Task.FromResult(VoidResult<string>.AsError("Secret cannot exceed 8192 characters."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => UpsertCoreAsync(db, subjectId, sessionId, trimmedKey, trimmedDescription, secret, ct),
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<string>, string>> ListKeysAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(Result<IReadOnlyList<string>, string>.AsError("Session id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListKeysCoreAsync(db, subjectId, sessionId, ct),
            cancellationToken);
    }

    public Task<Result<string, string>> GetAsync(
        Guid sessionId,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(Result<string, string>.AsError("Session id is required."));

        var trimmedKey = key?.Trim() ?? "";
        if (trimmedKey.Length == 0)
            return Task.FromResult(Result<string, string>.AsError("Key is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => GetCoreAsync(db, subjectId, sessionId, trimmedKey, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(
        Guid sessionId,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Session id is required."));

        var trimmedKey = key?.Trim() ?? "";
        if (trimmedKey.Length == 0)
            return Task.FromResult(VoidResult<string>.AsError("Key is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => DeleteCoreAsync(db, subjectId, sessionId, trimmedKey, ct),
            cancellationToken);
    }

    private static async Task<VoidResult<string>> UpsertCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        string key,
        string description,
        string secret,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken).ConfigureAwait(false))
                return VoidResult<string>.AsError($"Session '{sessionId}' not found.");

            var entity = await db.SessionSecrets
                .FirstOrDefaultAsync(
                    x => x.SubjectId == subjectId && x.SessionId == sessionId && x.Key == key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                entity = new DysonSessionSecretEntity
                {
                    Id = Guid.NewGuid(),
                    SubjectId = subjectId,
                    SessionId = sessionId,
                    Key = key,
                };
                db.SessionSecrets.Add(entity);
            }

            entity.Description = description;
            entity.Secret = secret;
            entity.UpdatedUtc = DateTime.UtcNow;
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            // ex.Message only; EF does not put parameter values in messages unless sensitive logging is on.
            return VoidResult<string>.AsError($"Failed to store secret '{key}': {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<string>, string>> ListKeysCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken).ConfigureAwait(false))
                return Result<IReadOnlyList<string>, string>.AsError($"Session '{sessionId}' not found.");

            var keys = await db.SessionSecrets
                .AsNoTracking()
                .Where(x => x.SubjectId == subjectId && x.SessionId == sessionId)
                .Select(x => x.Key)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            keys.Sort(StringComparer.Ordinal);
            return Result<IReadOnlyList<string>, string>.AsValue(keys);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<string>, string>.AsError($"Failed to list secret keys: {ex.Message}");
        }
    }

    private static async Task<Result<string, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken).ConfigureAwait(false))
                return Result<string, string>.AsError($"Session '{sessionId}' not found.");

            var secret = await db.SessionSecrets
                .AsNoTracking()
                .Where(x => x.SubjectId == subjectId && x.SessionId == sessionId && x.Key == key)
                .Select(x => x.Secret)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return secret is null
                ? Result<string, string>.AsError($"No secret stored for key '{key}'.")
                : Result<string, string>.AsValue(secret);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<string, string>.AsError($"Failed to read secret '{key}': {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken).ConfigureAwait(false))
                return VoidResult<string>.AsError($"Session '{sessionId}' not found.");

            await db.SessionSecrets
                .Where(x => x.SubjectId == subjectId && x.SessionId == sessionId && x.Key == key)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to delete secret '{key}': {ex.Message}");
        }
    }

    private static async Task<bool> SessionOwnedAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await db.Sessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
            .ConfigureAwait(false);
}

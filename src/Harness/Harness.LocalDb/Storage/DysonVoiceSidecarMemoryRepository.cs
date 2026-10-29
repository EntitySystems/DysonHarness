using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonVoiceSidecarMemoryRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonVoiceSidecarMemoryRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<Result<DysonVoiceSidecarMemory, string>> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(Result<DysonVoiceSidecarMemory, string>.AsError("Session id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => GetCoreAsync(db, subjectId, sessionId, ct),
            cancellationToken);
    }

    public Task<Result<DysonVoiceSidecarMemory, string>> ApplyAsync(
        Guid sessionId,
        Func<string, Result<string, string>> transform,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Task.FromResult(Result<DysonVoiceSidecarMemory, string>.AsError("Session id is required."));
        ArgumentNullException.ThrowIfNull(transform);

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ApplyCoreAsync(db, subjectId, sessionId, transform, ct),
            cancellationToken);
    }

    private static async Task<Result<DysonVoiceSidecarMemory, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<DysonVoiceSidecarMemory, string>.AsError($"Session '{sessionId}' not found.");

            var entity = await db.VoiceSidecarMemories
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.SessionId == sessionId && x.SubjectId == subjectId,
                    cancellationToken)
                .ConfigureAwait(false);

            return Result<DysonVoiceSidecarMemory, string>.AsValue(
                entity is null ? Empty(sessionId) : ToModel(entity));
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonVoiceSidecarMemory, string>.AsError(
                $"Failed to load voice sidecar memory: {ex.Message}");
        }
    }

    private static async Task<Result<DysonVoiceSidecarMemory, string>> ApplyCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        Func<string, Result<string, string>> transform,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await SessionOwnedAsync(db, subjectId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<DysonVoiceSidecarMemory, string>.AsError($"Session '{sessionId}' not found.");

            var entity = await db.VoiceSidecarMemories
                .FirstOrDefaultAsync(
                    x => x.SessionId == sessionId && x.SubjectId == subjectId,
                    cancellationToken)
                .ConfigureAwait(false);

            var current = entity is null ? Empty(sessionId) : ToModel(entity);
            var transformed = transform(current.Content);
            if (transformed.IsError)
                return Result<DysonVoiceSidecarMemory, string>.AsError(transformed.Error);

            var tooLong = OverCap(transformed.Value);
            if (tooLong is not null)
                return Result<DysonVoiceSidecarMemory, string>.AsError(tooLong);

            var written = await TryWriteAsync(
                    db, subjectId, sessionId, entity, current.Version, transformed.Value, cancellationToken)
                .ConfigureAwait(false);
            if (written is not null)
                return Result<DysonVoiceSidecarMemory, string>.AsValue(written);

            // Version moved under us. One retry against the fresh row; a second miss is an error.
            db.ChangeTracker.Clear();
            var fresh = await db.VoiceSidecarMemories
                .FirstOrDefaultAsync(
                    x => x.SessionId == sessionId && x.SubjectId == subjectId,
                    cancellationToken)
                .ConfigureAwait(false);
            var freshModel = fresh is null ? Empty(sessionId) : ToModel(fresh);
            var retried = transform(freshModel.Content);
            if (retried.IsError)
                return Result<DysonVoiceSidecarMemory, string>.AsError(retried.Error);

            var retryTooLong = OverCap(retried.Value);
            if (retryTooLong is not null)
                return Result<DysonVoiceSidecarMemory, string>.AsError(retryTooLong);

            var second = await TryWriteAsync(
                    db, subjectId, sessionId, fresh, freshModel.Version, retried.Value, cancellationToken)
                .ConfigureAwait(false);
            return second is not null
                ? Result<DysonVoiceSidecarMemory, string>.AsValue(second)
                : Result<DysonVoiceSidecarMemory, string>.AsError(
                    "Voice sidecar memory changed while saving. Try again.");
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonVoiceSidecarMemory, string>.AsError(
                $"Failed to save voice sidecar memory: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> at <c>Version + 1</c> only when the stored version is still
    /// <paramref name="expectedVersion"/> (insert when 0). Returns null when the version moved.
    /// </summary>
    private static async Task<DysonVoiceSidecarMemory?> TryWriteAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        DysonVoiceSidecarMemoryEntity? tracked,
        int expectedVersion,
        string content,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (expectedVersion == 0)
        {
            if (tracked is not null)
                return null;

            var inserted = new DysonVoiceSidecarMemoryEntity
            {
                Id = Guid.NewGuid(),
                SubjectId = subjectId,
                SessionId = sessionId,
                Content = content,
                Version = 1,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.VoiceSidecarMemories.Add(inserted);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return ToModel(inserted);
        }

        if (tracked is null || tracked.Version != expectedVersion)
            return null;

        tracked.Content = content;
        tracked.Version = expectedVersion + 1;
        tracked.UpdatedUtc = now;
        await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
        return ToModel(tracked);
    }

    private static string? OverCap(string content) =>
        content.Length > DysonVoiceSidecarMemoryLimits.MaxChars
            ? "Memory cannot grow past 16,384 characters."
            : null;

    private static async Task<bool> SessionOwnedAsync(
        DysonDbContext db,
        string subjectId,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await db.Sessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.SubjectId == subjectId, cancellationToken)
            .ConfigureAwait(false);

    private static DysonVoiceSidecarMemory Empty(Guid sessionId) =>
        new(sessionId, "", 0, null);

    private static DysonVoiceSidecarMemory ToModel(DysonVoiceSidecarMemoryEntity entity) =>
        new(entity.SessionId, entity.Content, entity.Version, entity.UpdatedUtc);
}

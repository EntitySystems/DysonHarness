using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonMetaScratchFileRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonMetaScratchFileRepository
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    public Task<Result<IReadOnlyList<DysonMetaScratchFile>, string>> ListAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Task.FromResult(Result<IReadOnlyList<DysonMetaScratchFile>, string>.AsError(
                "Work directory id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListCoreAsync(db, subjectId, workDirectoryId, ct),
            cancellationToken);
    }

    public Task<Result<DysonMetaScratchFile, string>> GetAsync(
        long id,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        var precondition = ValidateLookup(id, workDirectoryId);
        if (precondition.IsError)
            return Task.FromResult(Result<DysonMetaScratchFile, string>.AsError(precondition.Error));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => GetCoreAsync(db, subjectId, id, workDirectoryId, ct),
            cancellationToken);
    }

    public Task<Result<long, string>> CreateAsync(
        Guid workDirectoryId,
        string title,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Task.FromResult(Result<long, string>.AsError("Work directory id is required."));

        var titleResult = ValidateTitle(title);
        if (titleResult.IsError)
            return Task.FromResult(Result<long, string>.AsError(titleResult.Error));

        if (content is null)
            return Task.FromResult(Result<long, string>.AsError("Content is required."));

        var tooLong = OverCap(content);
        if (tooLong is not null)
            return Task.FromResult(Result<long, string>.AsError(tooLong));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => CreateCoreAsync(db, subjectId, workDirectoryId, titleResult.Value, content, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> UpdateAsync(
        long id,
        Guid workDirectoryId,
        string? title,
        string? content,
        CancellationToken cancellationToken = default)
    {
        var precondition = ValidateLookup(id, workDirectoryId);
        if (precondition.IsError)
            return Task.FromResult(precondition);

        if (title is null && content is null)
            return Task.FromResult(VoidResult<string>.AsError("Nothing to update."));

        string? trimmedTitle = null;
        if (title is not null)
        {
            var titleResult = ValidateTitle(title);
            if (titleResult.IsError)
                return Task.FromResult(VoidResult<string>.AsError(titleResult.Error));
            trimmedTitle = titleResult.Value;
        }

        if (content is not null)
        {
            var tooLong = OverCap(content);
            if (tooLong is not null)
                return Task.FromResult(VoidResult<string>.AsError(tooLong));
        }

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => UpdateCoreAsync(db, subjectId, id, workDirectoryId, trimmedTitle, content, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(
        long id,
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        var precondition = ValidateLookup(id, workDirectoryId);
        if (precondition.IsError)
            return Task.FromResult(precondition);

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => DeleteCoreAsync(db, subjectId, id, workDirectoryId, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> MoveAsync(
        long id,
        Guid workDirectoryId,
        int toPosition,
        CancellationToken cancellationToken = default)
    {
        var precondition = ValidateLookup(id, workDirectoryId);
        if (precondition.IsError)
            return Task.FromResult(precondition);

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => MoveCoreAsync(db, subjectId, id, workDirectoryId, toPosition, ct),
            cancellationToken);
    }

    private static async Task<Result<IReadOnlyList<DysonMetaScratchFile>, string>> ListCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<IReadOnlyList<DysonMetaScratchFile>, string>.AsError(
                    $"Work directory '{workDirectoryId}' not found.");

            var rows = await db.MetaScratchFiles
                .AsNoTracking()
                .Where(x => x.WorkDirectoryId == workDirectoryId)
                .OrderBy(x => x.SortIndex == null)
                .ThenBy(x => x.SortIndex)
                .ThenByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.Id)
                .Select(x => new DysonMetaScratchFile
                {
                    Id = x.Id,
                    WorkDirectoryId = x.WorkDirectoryId,
                    Title = x.Title,
                    Content = null,
                    ContentLength = x.Content.Length,
                    SortIndex = x.SortIndex,
                    CreatedUtc = x.CreatedUtc,
                    UpdatedUtc = x.UpdatedUtc,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonMetaScratchFile>, string>.AsValue(rows);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonMetaScratchFile>, string>.AsError(
                $"Failed to list scratch file: {ex.Message}");
        }
    }

    private static async Task<Result<DysonMetaScratchFile, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        long id,
        Guid workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<DysonMetaScratchFile, string>.AsError(
                    $"Work directory '{workDirectoryId}' not found.");

            var entity = await db.MetaScratchFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == id && x.WorkDirectoryId == workDirectoryId,
                    cancellationToken)
                .ConfigureAwait(false);

            return entity is null
                ? Result<DysonMetaScratchFile, string>.AsError($"Scratch file '{id}' not found.")
                : Result<DysonMetaScratchFile, string>.AsValue(ToModel(entity, includeContent: true));
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonMetaScratchFile, string>.AsError($"Failed to load scratch file: {ex.Message}");
        }
    }

    private static async Task<Result<long, string>> CreateCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        string title,
        string content,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return Result<long, string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var now = DateTime.UtcNow;
            var entity = new DysonMetaScratchFileEntity
            {
                WorkDirectoryId = workDirectoryId,
                Title = title,
                Content = content,
                SortIndex = null,
                CreatedUtc = now,
                UpdatedUtc = now,
            };

            db.MetaScratchFiles.Add(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);

            if (entity.Id <= 0)
                return Result<long, string>.AsError("Failed to allocate scratch file id.");

            return Result<long, string>.AsValue(entity.Id);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<long, string>.AsError($"Failed to create scratch file: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> UpdateCoreAsync(
        DysonDbContext db,
        string subjectId,
        long id,
        Guid workDirectoryId,
        string? title,
        string? content,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var entity = await db.MetaScratchFiles
                .FirstOrDefaultAsync(
                    x => x.Id == id && x.WorkDirectoryId == workDirectoryId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return VoidResult<string>.AsError($"Scratch file '{id}' not found.");

            if (title is not null)
                entity.Title = title;
            if (content is not null)
                entity.Content = content;

            entity.UpdatedUtc = DateTime.UtcNow;
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to update scratch file: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        string subjectId,
        long id,
        Guid workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Work directory '{workDirectoryId}' not found.");

            var entity = await db.MetaScratchFiles
                .FirstOrDefaultAsync(
                    x => x.Id == id && x.WorkDirectoryId == workDirectoryId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return VoidResult<string>.AsError($"Scratch file '{id}' not found.");

            db.MetaScratchFiles.Remove(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to delete scratch file: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> MoveCoreAsync(
        DysonDbContext db,
        string subjectId,
        long id,
        Guid workDirectoryId,
        int toPosition,
        CancellationToken cancellationToken)
    {
        try
        {
            var owned = await WorkDirectoryOwnedAsync(db, subjectId, workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!owned)
                return VoidResult<string>.AsError($"Work directory '{workDirectoryId}' not found.");

            // ponytail: rewrites every row, O(n) per move, fine for tens of files; upgrade to sparse indexes if counts grow.
            var rows = await db.MetaScratchFiles
                .Where(x => x.WorkDirectoryId == workDirectoryId)
                .OrderBy(x => x.SortIndex == null)
                .ThenBy(x => x.SortIndex)
                .ThenByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0)
                return VoidResult<string>.AsError($"Scratch file '{id}' not found.");

            var moved = rows[index];
            rows.RemoveAt(index);
            rows.Insert(Math.Clamp(toPosition, 0, rows.Count), moved);

            for (var i = 0; i < rows.Count; i++)
                rows[i].SortIndex = i;

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to move scratch file: {ex.Message}");
        }
    }

    private static VoidResult<string> ValidateLookup(long id, Guid workDirectoryId)
    {
        if (id <= 0)
            return VoidResult<string>.AsError("Scratch file id must be a positive integer.");
        if (workDirectoryId == Guid.Empty)
            return VoidResult<string>.AsError("Work directory id is required.");
        return VoidResult<string>.Success;
    }

    /// <summary>Blank or over 200 chars after trim. Success value is the trimmed title.</summary>
    private static Result<string, string> ValidateTitle(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length == 0)
            return Result<string, string>.AsError("Title is required.");
        if (trimmed.Length > 200)
            return Result<string, string>.AsError("Title must be at most 200 characters.");
        return Result<string, string>.AsValue(trimmed);
    }

    private static string? OverCap(string content) =>
        content.Length > IDysonMetaScratchFileRepository.MaxContentChars
            ? $"Scratch file content is {content.Length} characters; the maximum is 131072 (128K). Nothing was saved."
            : null;

    private static async Task<bool> WorkDirectoryOwnedAsync(
        DysonDbContext db,
        string subjectId,
        Guid workDirectoryId,
        CancellationToken cancellationToken) =>
        await db.WorkDirectories
            .AsNoTracking()
            .AnyAsync(w => w.Id == workDirectoryId && w.SubjectId == subjectId, cancellationToken)
            .ConfigureAwait(false);

    private static DysonMetaScratchFile ToModel(DysonMetaScratchFileEntity entity, bool includeContent) => new()
    {
        Id = entity.Id,
        WorkDirectoryId = entity.WorkDirectoryId,
        Title = entity.Title,
        Content = includeContent ? entity.Content : null,
        ContentLength = entity.Content.Length,
        SortIndex = entity.SortIndex,
        CreatedUtc = entity.CreatedUtc,
        UpdatedUtc = entity.UpdatedUtc,
    };
}

using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonWorkDirectoryStore(DysonDbAccessor accessor)
{
    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));

    public Task<Result<Guid, string>> CreateAsync(
        string absolutePath,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Task.FromResult(Result<Guid, string>.AsError("Path is required."));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<Guid, string>.AsError($"Invalid path: {ex.Message}"));
        }

        if (!Directory.Exists(fullPath))
            return Task.FromResult(Result<Guid, string>.AsError("Directory does not exist."));

        var displayName = string.IsNullOrWhiteSpace(name)
            ? new DirectoryInfo(fullPath).Name
            : name.Trim();

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = fullPath;

        return _accessor.RunAsync(
            (db, ct) => CreateCoreAsync(db, fullPath, displayName, ct),
            cancellationToken);
    }

    public Task<Result<DysonWorkDirectoryEntity, string>> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => _accessor.RunAsync((db, ct) => GetCoreAsync(db, id, ct), cancellationToken);

    public Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListAsync(
        CancellationToken cancellationToken = default)
        => _accessor.RunAsync(ListCoreAsync, cancellationToken);

    public Task<VoidResult<string>> TouchOpenedAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => _accessor.RunAsync((db, ct) => TouchOpenedCoreAsync(db, id, ct), cancellationToken);

    /// <summary>
    /// Removes the work directory registration. Blocked when any sessions still reference it.
    /// Does not delete the folder on disk.
    /// </summary>
    public Task<VoidResult<string>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => _accessor.RunAsync((db, ct) => DeleteCoreAsync(db, id, ct), cancellationToken);

    private static async Task<Result<Guid, string>> CreateCoreAsync(
        DysonDbContext db,
        string fullPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await db.WorkDirectories
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.AbsolutePath == fullPath, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
                return Result<Guid, string>.AsError($"Work directory already registered: {fullPath}");

            var now = DateTime.UtcNow;
            var entity = new DysonWorkDirectoryEntity
            {
                Id = Guid.NewGuid(),
                Name = displayName,
                AbsolutePath = fullPath,
                CreatedUtc = now,
                LastOpenedUtc = now,
            };

            db.WorkDirectories.Add(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(entity.Id);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<Guid, string>.AsError($"Failed to create work directory: {ex.Message}");
        }
    }

    private static async Task<Result<DysonWorkDirectoryEntity, string>> GetCoreAsync(
        DysonDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.WorkDirectories
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
                .ConfigureAwait(false);

            return entity is null
                ? Result<DysonWorkDirectoryEntity, string>.AsError($"Work directory '{id}' not found.")
                : Result<DysonWorkDirectoryEntity, string>.AsValue(entity);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonWorkDirectoryEntity, string>.AsError(
                $"Failed to load work directory: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListCoreAsync(
        DysonDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            var list = await db.WorkDirectories
                .AsNoTracking()
                .OrderByDescending(w => w.LastOpenedUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>.AsError(
                $"Failed to list work directories: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> TouchOpenedCoreAsync(
        DysonDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.WorkDirectories
                .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return new VoidResult<string>($"Work directory '{id}' not found.");

            entity.LastOpenedUtc = DateTime.UtcNow;
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to update work directory: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.WorkDirectories
                .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return new VoidResult<string>($"Work directory '{id}' not found.");

            var sessionCount = await db.Sessions
                .CountAsync(s => s.WorkDirectoryId == id, cancellationToken)
                .ConfigureAwait(false);

            if (sessionCount > 0)
            {
                return new VoidResult<string>(
                    $"Cannot remove work directory while {sessionCount} session(s) still reference it.");
            }

            db.WorkDirectories.Remove(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return new VoidResult<string>($"Failed to delete work directory: {ex.Message}");
        }
    }
}

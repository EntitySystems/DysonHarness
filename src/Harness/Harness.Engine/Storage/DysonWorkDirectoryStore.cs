using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed class DysonWorkDirectoryStore(DysonDbContext db)
{
    private readonly DysonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<Result<Guid, string>> CreateAsync(
        string absolutePath,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Result<Guid, string>.AsError("Path is required.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<Guid, string>.AsError($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            return Result<Guid, string>.AsError("Directory does not exist.");

        var displayName = string.IsNullOrWhiteSpace(name)
            ? new DirectoryInfo(fullPath).Name
            : name.Trim();

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = fullPath;

        try
        {
            var existing = await _db.WorkDirectories
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

            _db.WorkDirectories.Add(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(entity.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid, string>.AsError($"Failed to create work directory: {ex.Message}");
        }
    }

    public async Task<Result<DysonWorkDirectoryEntity, string>> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await _db.WorkDirectories
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
                .ConfigureAwait(false);

            return entity is null
                ? Result<DysonWorkDirectoryEntity, string>.AsError($"Work directory '{id}' not found.")
                : Result<DysonWorkDirectoryEntity, string>.AsValue(entity);
        }
        catch (Exception ex)
        {
            return Result<DysonWorkDirectoryEntity, string>.AsError(
                $"Failed to load work directory: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await _db.WorkDirectories
                .AsNoTracking()
                .OrderByDescending(w => w.LastOpenedUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>.AsValue(list);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>.AsError(
                $"Failed to list work directories: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> TouchOpenedAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await _db.WorkDirectories
                .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return new VoidResult<string>($"Work directory '{id}' not found.");

            entity.LastOpenedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to update work directory: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the work directory registration. Blocked when any sessions still reference it.
    /// Does not delete the folder on disk.
    /// </summary>
    public async Task<VoidResult<string>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await _db.WorkDirectories
                .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (entity is null)
                return new VoidResult<string>($"Work directory '{id}' not found.");

            var sessionCount = await _db.Sessions
                .CountAsync(s => s.WorkDirectoryId == id, cancellationToken)
                .ConfigureAwait(false);

            if (sessionCount > 0)
            {
                return new VoidResult<string>(
                    $"Cannot remove work directory while {sessionCount} session(s) still reference it.");
            }

            _db.WorkDirectories.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to delete work directory: {ex.Message}");
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace DysonHarness;

public sealed partial class DysonPluginInstallationRepository(
    DysonDbAccessor accessor,
    IDysonSubjectContext subjectContext) : IDysonPluginInstallationRepository
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly DysonDbAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private readonly IDysonSubjectContext _subjectContext =
        subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NormalizedPluginIdRegex();

    public Task<Result<Guid, string>> UpsertAsync(
        DysonPluginInstallationEntity installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var validation = Validate(installation);
        if (validation.IsError)
            return Task.FromResult(Result<Guid, string>.AsError(validation.Error));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => UpsertCoreAsync(db, subjectId, installation, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> ReplaceAsync(
        Guid id,
        DysonPluginInstallationEntity installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (id == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Plugin installation id is required."));

        var validation = Validate(installation);
        if (validation.IsError)
            return Task.FromResult(validation);

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ReplaceCoreAsync(db, subjectId, id, installation, ct),
            cancellationToken);
    }

    public Task<Result<DysonPluginInstallationEntity, string>> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return Task.FromResult(Result<DysonPluginInstallationEntity, string>.AsError(
                "Plugin installation id is required."));
        }

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => GetCoreAsync(db, subjectId, id, ct), cancellationToken);
    }

    public Task<Result<IReadOnlyList<DysonPluginInstallationEntity>, string>> ListAsync(
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
        {
            return Task.FromResult(Result<IReadOnlyList<DysonPluginInstallationEntity>, string>.AsError(
                "Work directory id must be non-empty when specified."));
        }

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => ListCoreAsync(db, subjectId, workDirectoryId, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> SetEnabledAsync(
        Guid id,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Plugin installation id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync(
            (db, ct) => SetEnabledCoreAsync(db, subjectId, id, isEnabled, ct),
            cancellationToken);
    }

    public Task<VoidResult<string>> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Plugin installation id is required."));

        var subjectId = _subjectContext.SubjectId;
        return _accessor.RunAsync((db, ct) => DeleteCoreAsync(db, subjectId, id, ct), cancellationToken);
    }

    private static VoidResult<string> Validate(DysonPluginInstallationEntity installation)
    {
        if (string.IsNullOrWhiteSpace(installation.NormalizedPluginId) ||
            !NormalizedPluginIdRegex().IsMatch(installation.NormalizedPluginId))
        {
            return VoidResult<string>.AsError(
                "Normalized plugin id must be 1-64 lowercase letters, digits, dots, underscores, or hyphens, " +
                "and must begin and end with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(installation.DisplayName))
            return VoidResult<string>.AsError("Plugin display name is required.");
        if (!DysonPluginStorageValues.SourceKinds.Contains(installation.SourceKind))
            return VoidResult<string>.AsError($"Unsupported plugin source kind: {installation.SourceKind}.");
        if (string.IsNullOrWhiteSpace(installation.SourceLocation))
            return VoidResult<string>.AsError("Plugin source location is required.");
        if (!DysonPluginStorageValues.PackageFormats.Contains(installation.PackageFormat))
            return VoidResult<string>.AsError($"Unsupported plugin package format: {installation.PackageFormat}.");
        if (!DysonPluginStorageValues.Statuses.Contains(installation.Status))
            return VoidResult<string>.AsError($"Unsupported plugin status: {installation.Status}.");

        if (installation.InstallScope == DysonPluginStorageValues.ProjectScope)
        {
            if (installation.WorkDirectoryId is null || installation.WorkDirectoryId == Guid.Empty)
            {
                return VoidResult<string>.AsError(
                    "Project plugin installation records require an owning work directory id.");
            }
        }
        else if (installation.InstallScope == DysonPluginStorageValues.GlobalScope)
        {
            if (installation.WorkDirectoryId is not null)
            {
                return VoidResult<string>.AsError(
                    "Global plugin installation records must not have a work directory id.");
            }
        }
        else
        {
            return VoidResult<string>.AsError($"Unsupported plugin install scope: {installation.InstallScope}.");
        }

        if (string.IsNullOrWhiteSpace(installation.PackageRoot) ||
            !Path.IsPathFullyQualified(installation.PackageRoot))
        {
            return VoidResult<string>.AsError("Plugin package root must be an absolute path.");
        }

        var inventory = ValidateJson(installation.ComponentInventoryJson, "component inventory", allowNull: false);
        if (inventory.IsError)
            return inventory;
        var config = ValidateJson(installation.ConfigurationSchemaJson, "configuration schema", allowNull: true);
        if (config.IsError)
            return config;
        return ValidateJson(installation.DiagnosticsJson, "diagnostics", allowNull: false);
    }

    private static VoidResult<string> ValidateJson(string? json, string label, bool allowNull)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return allowNull
                ? VoidResult<string>.Success
                : VoidResult<string>.AsError($"Plugin {label} JSON is required.");
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
            return VoidResult<string>.Success;
        }
        catch (JsonException ex)
        {
            return VoidResult<string>.AsError($"Invalid plugin {label} JSON: {ex.Message}");
        }
    }

    private static async Task<Result<Guid, string>> UpsertCoreAsync(
        DysonDbContext db,
        string subjectId,
        DysonPluginInstallationEntity input,
        CancellationToken cancellationToken)
    {
        try
        {
            DysonWorkDirectoryEntity? workDirectory = null;
            if (input.WorkDirectoryId is Guid workDirectoryId)
            {
                workDirectory = await db.WorkDirectories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        w => w.Id == workDirectoryId && w.SubjectId == subjectId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (workDirectory is null)
                {
                    return Result<Guid, string>.AsError(
                        $"Work directory '{workDirectoryId}' not found for the current subject.");
                }

                var ownership = ValidateProjectPackageRoot(workDirectory.AbsolutePath, input.PackageRoot);
                if (ownership.IsError)
                    return Result<Guid, string>.AsError(ownership.Error);
            }

            var existing = await db.PluginInstallations
                .FirstOrDefaultAsync(
                    p => p.SubjectId == subjectId &&
                         p.NormalizedPluginId == input.NormalizedPluginId &&
                         p.InstallScope == input.InstallScope &&
                         p.WorkDirectoryId == input.WorkDirectoryId,
                    cancellationToken)
                .ConfigureAwait(false);

            var now = DateTime.UtcNow;
            if (existing is null)
            {
                existing = new DysonPluginInstallationEntity
                {
                    Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id,
                    SubjectId = subjectId,
                    NormalizedPluginId = input.NormalizedPluginId,
                    InstalledUtc = now,
                };
                db.PluginInstallations.Add(existing);
            }

            CopyMutableValues(input, existing);
            existing.SubjectId = subjectId;
            existing.InstalledUtc = existing.InstalledUtc == default ? now : existing.InstalledUtc;
            existing.UpdatedUtc = now;

            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return Result<Guid, string>.AsValue(existing.Id);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<Guid, string>.AsError($"Failed to save plugin installation: {ex.Message}");
        }
    }

    private static void CopyMutableValues(
        DysonPluginInstallationEntity source,
        DysonPluginInstallationEntity destination)
    {
        destination.NormalizedPluginId = source.NormalizedPluginId;
        destination.DisplayName = source.DisplayName.Trim();
        destination.Version = NormalizeOptional(source.Version);
        destination.SourceKind = source.SourceKind;
        destination.SourceLocation = source.SourceLocation.Trim();
        destination.RequestedRef = NormalizeOptional(source.RequestedRef);
        destination.SourceSubdirectory = NormalizeOptional(source.SourceSubdirectory);
        destination.ResolvedCommit = NormalizeOptional(source.ResolvedCommit);
        destination.ContentChecksum = NormalizeOptional(source.ContentChecksum);
        destination.PackageFormat = source.PackageFormat;
        destination.SchemaVersion = NormalizeOptional(source.SchemaVersion);
        destination.InstallScope = source.InstallScope;
        destination.WorkDirectoryId = source.WorkDirectoryId;
        destination.IsEnabled = source.IsEnabled;
        destination.Status = source.Status;
        destination.PackageRoot = Path.GetFullPath(source.PackageRoot.Trim());
        destination.ComponentInventoryJson = source.ComponentInventoryJson;
        destination.ConfigurationSchemaJson = NormalizeOptional(source.ConfigurationSchemaJson);
        destination.DiagnosticsJson = source.DiagnosticsJson;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static VoidResult<string> ValidateProjectPackageRoot(
        string workDirectoryRoot,
        string packageRoot)
    {
        try
        {
            var expectedRoot = Path.GetFullPath(Path.Combine(workDirectoryRoot, ".dyson", "plugins"));
            var actualRoot = Path.GetFullPath(packageRoot);
            var prefix = expectedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            if (!actualRoot.StartsWith(prefix, PathComparison))
            {
                return VoidResult<string>.AsError(
                    $"Project plugin package root must be owned by '{expectedRoot}'.");
            }

            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Invalid project plugin package root: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> ReplaceCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid id,
        DysonPluginInstallationEntity input,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await db.PluginInstallations
                .FirstOrDefaultAsync(p => p.Id == id && p.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                return VoidResult<string>.AsError($"Plugin installation '{id}' not found.");

            if (!string.Equals(existing.NormalizedPluginId, input.NormalizedPluginId, StringComparison.Ordinal) ||
                !string.Equals(existing.InstallScope, input.InstallScope, StringComparison.Ordinal) ||
                existing.WorkDirectoryId != input.WorkDirectoryId)
            {
                return VoidResult<string>.AsError(
                    "Plugin updates cannot change the installation identity, scope, or owning work directory.");
            }

            if (input.WorkDirectoryId is Guid workDirectoryId)
            {
                var workDirectory = await db.WorkDirectories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        w => w.Id == workDirectoryId && w.SubjectId == subjectId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (workDirectory is null)
                {
                    return VoidResult<string>.AsError(
                        $"Work directory '{workDirectoryId}' not found for the current subject.");
                }

                var ownership = ValidateProjectPackageRoot(workDirectory.AbsolutePath, input.PackageRoot);
                if (ownership.IsError)
                    return ownership;
            }

            CopyMutableValues(input, existing);
            existing.SubjectId = subjectId;
            existing.UpdatedUtc = DateTime.UtcNow;
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to replace plugin installation: {ex.Message}");
        }
    }

    private static async Task<Result<DysonPluginInstallationEntity, string>> GetCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.PluginInstallations
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                return Result<DysonPluginInstallationEntity, string>.AsError(
                    $"Plugin installation '{id}' not found.");
            }

            NormalizeUtcKinds(entity);
            return Result<DysonPluginInstallationEntity, string>.AsValue(entity);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<DysonPluginInstallationEntity, string>.AsError(
                $"Failed to load plugin installation: {ex.Message}");
        }
    }

    private static async Task<Result<IReadOnlyList<DysonPluginInstallationEntity>, string>> ListCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid? workDirectoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (workDirectoryId is Guid requestedWorkDirectoryId)
            {
                var owned = await db.WorkDirectories
                    .AsNoTracking()
                    .AnyAsync(
                        w => w.Id == requestedWorkDirectoryId && w.SubjectId == subjectId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!owned)
                {
                    return Result<IReadOnlyList<DysonPluginInstallationEntity>, string>.AsError(
                        $"Work directory '{requestedWorkDirectoryId}' not found for the current subject.");
                }
            }

            var query = db.PluginInstallations
                .AsNoTracking()
                .Where(p => p.SubjectId == subjectId);
            query = workDirectoryId is null
                ? query.Where(p => p.InstallScope == DysonPluginStorageValues.GlobalScope)
                : query.Where(p => p.InstallScope == DysonPluginStorageValues.GlobalScope ||
                                   p.WorkDirectoryId == workDirectoryId);

            var list = await query
                .OrderBy(p => p.NormalizedPluginId)
                .ThenBy(p => p.InstallScope)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var entity in list)
                NormalizeUtcKinds(entity);

            return Result<IReadOnlyList<DysonPluginInstallationEntity>, string>.AsValue(list);
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return Result<IReadOnlyList<DysonPluginInstallationEntity>, string>.AsError(
                $"Failed to list plugin installations: {ex.Message}");
        }
    }

    private static void NormalizeUtcKinds(DysonPluginInstallationEntity entity)
    {
        entity.InstalledUtc = DateTime.SpecifyKind(entity.InstalledUtc, DateTimeKind.Utc);
        entity.UpdatedUtc = DateTime.SpecifyKind(entity.UpdatedUtc, DateTimeKind.Utc);
    }

    private static async Task<VoidResult<string>> SetEnabledCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid id,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.PluginInstallations
                .FirstOrDefaultAsync(p => p.Id == id && p.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
                return VoidResult<string>.AsError($"Plugin installation '{id}' not found.");

            entity.IsEnabled = isEnabled;
            entity.Status = isEnabled ? "Installed" : "Disabled";
            entity.UpdatedUtc = DateTime.UtcNow;
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to update plugin installation: {ex.Message}");
        }
    }

    private static async Task<VoidResult<string>> DeleteCoreAsync(
        DysonDbContext db,
        string subjectId,
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await db.PluginInstallations
                .FirstOrDefaultAsync(p => p.Id == id && p.SubjectId == subjectId, cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
                return VoidResult<string>.AsError($"Plugin installation '{id}' not found.");

            db.PluginInstallations.Remove(entity);
            await DysonDbAccessor.SaveChangesAsync(db, cancellationToken).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (Exception ex) when (!DysonDbAccessor.IsSqliteBusyOrLocked(ex))
        {
            return VoidResult<string>.AsError($"Failed to delete plugin installation: {ex.Message}");
        }
    }
}

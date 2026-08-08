namespace DysonHarness;

public enum DysonPluginDataDisposition
{
    Retain = 0,
    Delete = 1,
}

public enum DysonPluginCatalogChangeKind
{
    Installed = 0,
    Enabled = 1,
    Disabled = 2,
    Uninstalled = 3,
}

/// <summary>Raised after a persisted lifecycle operation so only affected session catalogs rebuild.</summary>
public sealed class DysonPluginCatalogChangedEventArgs : EventArgs
{
    public required DysonPluginCatalogChangeKind Kind { get; init; }
    public required Guid InstallationId { get; init; }
    public required string NormalizedPluginId { get; init; }
    public required DysonPluginInstallScope Scope { get; init; }
    public Guid? WorkDirectoryId { get; init; }
}

/// <summary>
/// Uninstall requires the original scope target, preventing a caller from using one scope's path
/// context to delete a package or PLUGIN_DATA owned by another scope.
/// </summary>
public sealed record DysonPluginUninstallRequest
{
    public required Guid InstallationId { get; init; }
    public required DysonPluginInstallTarget Target { get; init; }
    public required DysonPluginDataDisposition PluginDataDisposition { get; init; }

    public VoidResult<string> Validate()
    {
        if (InstallationId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin installation id is required.");
        if (Target is null)
            return VoidResult<string>.AsError("Plugin install target is required.");
        if (!Enum.IsDefined(PluginDataDisposition))
            return VoidResult<string>.AsError(
                $"Unsupported plugin data disposition: {PluginDataDisposition}.");

        return Target.Validate();
    }
}

public sealed record DysonPluginUninstallResult
{
    public required Guid InstallationId { get; init; }
    public required bool PackageDeleted { get; init; }
    public required bool PluginDataDeleted { get; init; }
}

/// <summary>
/// Lifecycle operations over already-installed plugin records. This service never changes an
/// installation's scope or paths; moving between scopes remains a future explicit copy/install.
/// </summary>
public sealed class DysonPluginLifecycleService(IDysonPluginInstallationRepository installations)
{
    private readonly IDysonPluginInstallationRepository _installations =
        installations ?? throw new ArgumentNullException(nameof(installations));

    public event EventHandler<DysonPluginCatalogChangedEventArgs>? Changed;

    /// <summary>
    /// Acquisition calls this after it atomically promotes a package and persists its initial
    /// record, allowing global sessions or one project’s sessions to rebuild without polling.
    /// </summary>
    public async Task<VoidResult<string>> NotifyInstalledAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin installation id is required.");

        var installation = await _installations.GetAsync(installationId, cancellationToken)
            .ConfigureAwait(false);
        if (installation.IsError)
            return VoidResult<string>.AsError(installation.Error);

        RaiseChanged(installation.Value, DysonPluginCatalogChangeKind.Installed);
        return VoidResult<string>.Success;
    }

    public async Task<VoidResult<string>> SetEnabledAsync(
        Guid installationId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin installation id is required.");

        var installation = await _installations.GetAsync(installationId, cancellationToken)
            .ConfigureAwait(false);
        if (installation.IsError)
            return VoidResult<string>.AsError(installation.Error);

        if (isEnabled && !CanEnable(installation.Value.Status))
        {
            return VoidResult<string>.AsError(
                $"Plugin installation '{installationId}' is not ready to enable (status: {installation.Value.Status}).");
        }

        var changed = await _installations.SetEnabledAsync(installationId, isEnabled, cancellationToken)
            .ConfigureAwait(false);
        if (changed.IsError)
            return changed;

        RaiseChanged(installation.Value, isEnabled
            ? DysonPluginCatalogChangeKind.Enabled
            : DysonPluginCatalogChangeKind.Disabled);
        return VoidResult<string>.Success;
    }

    public async Task<Result<DysonPluginUninstallResult, string>> UninstallAsync(
        DysonPluginUninstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = request.Validate();
        if (validation.IsError)
            return Result<DysonPluginUninstallResult, string>.AsError(validation.Error);

        var installation = await _installations.GetAsync(request.InstallationId, cancellationToken)
            .ConfigureAwait(false);
        if (installation.IsError)
            return Result<DysonPluginUninstallResult, string>.AsError(installation.Error);

        var ownership = ValidateOwnership(installation.Value, request.Target);
        if (ownership.IsError)
            return Result<DysonPluginUninstallResult, string>.AsError(ownership.Error);

        var packageDeletion = DeleteOwnedDirectoryIfPresent(
            installation.Value.PackageRoot,
            "Plugin package");
        if (packageDeletion.IsError)
            return Result<DysonPluginUninstallResult, string>.AsError(packageDeletion.Error);

        var dataDeleted = false;
        if (request.PluginDataDisposition == DysonPluginDataDisposition.Delete)
        {
            var pluginDataRoot = GetOwnedPluginDataRoot(installation.Value, request.Target);
            if (pluginDataRoot.IsError)
                return Result<DysonPluginUninstallResult, string>.AsError(pluginDataRoot.Error);

            var dataDeletion = DeleteOwnedDirectoryIfPresent(pluginDataRoot.Value, "Plugin data");
            if (dataDeletion.IsError)
                return Result<DysonPluginUninstallResult, string>.AsError(dataDeletion.Error);
            dataDeleted = dataDeletion.Value;
        }

        var deleted = await _installations.DeleteAsync(request.InstallationId, cancellationToken)
            .ConfigureAwait(false);
        if (deleted.IsError)
        {
            return Result<DysonPluginUninstallResult, string>.AsError(
                $"Plugin files were removed but the installation record could not be deleted: {deleted.Error}");
        }

        RaiseChanged(installation.Value, DysonPluginCatalogChangeKind.Uninstalled);
        return Result<DysonPluginUninstallResult, string>.AsValue(new DysonPluginUninstallResult
        {
            InstallationId = request.InstallationId,
            PackageDeleted = packageDeletion.Value,
            PluginDataDeleted = dataDeleted,
        });
    }

    private static bool CanEnable(string status) =>
        string.Equals(status, nameof(DysonPluginStatus.Installed), StringComparison.Ordinal) ||
        string.Equals(status, nameof(DysonPluginStatus.Disabled), StringComparison.Ordinal) ||
        string.Equals(status, nameof(DysonPluginStatus.UpdateAvailable), StringComparison.Ordinal);

    private static VoidResult<string> ValidateOwnership(
        DysonPluginInstallationEntity installation,
        DysonPluginInstallTarget target)
    {
        var expectedScope = target.Scope == DysonPluginInstallScope.Project
            ? DysonPluginStorageValues.ProjectScope
            : DysonPluginStorageValues.GlobalScope;
        if (!string.Equals(installation.InstallScope, expectedScope, StringComparison.Ordinal))
        {
            return VoidResult<string>.AsError(
                "Plugin installation scope does not match the supplied uninstall target; cross-scope movement is not supported.");
        }

        if (target.Scope == DysonPluginInstallScope.Project &&
            installation.WorkDirectoryId != target.WorkDirectoryId)
        {
            return VoidResult<string>.AsError(
                "Plugin installation work directory does not match the supplied uninstall target.");
        }

        var packageOwnership = DysonPluginPaths.ValidatePackageRootOwnership(target, installation.PackageRoot);
        if (packageOwnership.IsError)
            return packageOwnership;

        var normalizedId = DysonPluginPaths.NormalizePluginId(installation.NormalizedPluginId);
        if (normalizedId.IsError)
            return VoidResult<string>.AsError(normalizedId.Error);

        var roots = DysonPluginPaths.GetScopeRoots(target);
        if (roots.IsError)
            return VoidResult<string>.AsError(roots.Error);

        try
        {
            var packageRelative = Path.GetRelativePath(roots.Value.PluginsRoot, installation.PackageRoot)
                .Replace('\\', '/');
            var segments = packageRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2 ||
                !string.Equals(segments[0], normalizedId.Value, PathComparison))
            {
                return VoidResult<string>.AsError(
                    "Plugin package root must identify exactly one owned plugin version directory.");
            }
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Invalid plugin package root: {ex.Message}");
        }

        return VoidResult<string>.Success;
    }

    private static Result<string, string> GetOwnedPluginDataRoot(
        DysonPluginInstallationEntity installation,
        DysonPluginInstallTarget target)
    {
        var normalizedId = DysonPluginPaths.NormalizePluginId(installation.NormalizedPluginId);
        if (normalizedId.IsError)
            return Result<string, string>.AsError(normalizedId.Error);

        if (target.Scope == DysonPluginInstallScope.Project)
        {
            var data = target.WorkspaceFileSystem!.ResolvePath(
                $"{DysonPluginPaths.ProjectPluginDataRelativeDirectory}/{normalizedId.Value}");
            return data.IsError
                ? Result<string, string>.AsError(data.Error)
                : Result<string, string>.AsValue(data.Value);
        }

        try
        {
            return Result<string, string>.AsValue(Path.Combine(
                DysonAppPaths.GetPluginDataDirectory(target.AppMode!.Value),
                normalizedId.Value));
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid plugin data root: {ex.Message}");
        }
    }

    private static Result<bool, string> DeleteOwnedDirectoryIfPresent(string path, string label)
    {
        try
        {
            if (File.Exists(path))
                return Result<bool, string>.AsError($"{label} root is a file, not a directory: '{path}'.");
            if (!Directory.Exists(path))
                return Result<bool, string>.AsValue(false);

            Directory.Delete(path, recursive: true);
            return Result<bool, string>.AsValue(true);
        }
        catch (Exception ex)
        {
            return Result<bool, string>.AsError($"Failed to delete {label.ToLowerInvariant()}: {ex.Message}");
        }
    }

    private void RaiseChanged(DysonPluginInstallationEntity installation, DysonPluginCatalogChangeKind kind)
    {
        var scope = string.Equals(
            installation.InstallScope,
            DysonPluginStorageValues.ProjectScope,
            StringComparison.Ordinal)
            ? DysonPluginInstallScope.Project
            : DysonPluginInstallScope.Global;
        Changed?.Invoke(this, new DysonPluginCatalogChangedEventArgs
        {
            Kind = kind,
            InstallationId = installation.Id,
            NormalizedPluginId = installation.NormalizedPluginId,
            Scope = scope,
            WorkDirectoryId = installation.WorkDirectoryId,
        });
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

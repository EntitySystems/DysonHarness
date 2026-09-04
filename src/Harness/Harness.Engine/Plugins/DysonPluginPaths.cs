using System.Text.RegularExpressions;

namespace DysonHarness;

public sealed class DysonPluginInstallTarget
{
    private DysonPluginInstallTarget(
        DysonPluginInstallScope scope,
        Guid? workDirectoryId,
        IDysonWorkspaceFileSystem? workspaceFileSystem,
        DysonAppMode? appMode)
    {
        Scope = scope;
        WorkDirectoryId = workDirectoryId;
        WorkspaceFileSystem = workspaceFileSystem;
        AppMode = appMode;
    }

    public DysonPluginInstallScope Scope { get; }
    public Guid? WorkDirectoryId { get; }
    public IDysonWorkspaceFileSystem? WorkspaceFileSystem { get; }
    public DysonAppMode? AppMode { get; }

    public static Result<DysonPluginInstallTarget, string> ForProject(
        Guid workDirectoryId,
        IDysonWorkspaceFileSystem workspaceFileSystem)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileSystem);

        var target = new DysonPluginInstallTarget(
            DysonPluginInstallScope.Project,
            workDirectoryId,
            workspaceFileSystem,
            appMode: null);
        var validation = target.Validate();
        return validation.IsError
            ? Result<DysonPluginInstallTarget, string>.AsError(validation.Error)
            : Result<DysonPluginInstallTarget, string>.AsValue(target);
    }

    public static Result<DysonPluginInstallTarget, string> ForGlobal(DysonAppMode appMode)
    {
        if (!Enum.IsDefined(appMode))
        {
            return Result<DysonPluginInstallTarget, string>.AsError(
                $"Unsupported Dyson app mode: {appMode}.");
        }

        var target = new DysonPluginInstallTarget(
            DysonPluginInstallScope.Global,
            workDirectoryId: null,
            workspaceFileSystem: null,
            appMode);
        return Result<DysonPluginInstallTarget, string>.AsValue(target);
    }

    public VoidResult<string> Validate()
    {
        if (Scope == DysonPluginInstallScope.Project)
        {
            if (WorkDirectoryId is null || WorkDirectoryId == Guid.Empty)
                return VoidResult<string>.AsError("Project plugin installs require a work directory id.");
            if (WorkspaceFileSystem is null || !WorkspaceFileSystem.IsInitialized)
            {
                return VoidResult<string>.AsError(
                    "Project plugin installs require an initialized workspace filesystem.");
            }
            if (AppMode is not null)
                return VoidResult<string>.AsError("Project plugin installs must not specify an app mode.");

            return VoidResult<string>.Success;
        }

        if (Scope == DysonPluginInstallScope.Global)
        {
            if (WorkDirectoryId is not null || WorkspaceFileSystem is not null)
            {
                return VoidResult<string>.AsError(
                    "Global plugin installs must not specify a work directory.");
            }
            if (AppMode is null)
                return VoidResult<string>.AsError("Global plugin installs require an app mode.");
            if (!Enum.IsDefined(AppMode.Value))
                return VoidResult<string>.AsError($"Unsupported Dyson app mode: {AppMode.Value}.");

            return VoidResult<string>.Success;
        }

        return VoidResult<string>.AsError($"Unsupported plugin install scope: {Scope}.");
    }
}

public sealed record DysonPluginScopeRoots
{
    public required DysonPluginInstallScope Scope { get; init; }
    public Guid? WorkDirectoryId { get; init; }
    public required string PluginsRoot { get; init; }
    public required string PluginDataRoot { get; init; }
}

public sealed record DysonPluginPathsResult
{
    public required DysonPluginScopeRoots ScopeRoots { get; init; }
    public required string NormalizedPluginId { get; init; }
    public required string VersionOrContentId { get; init; }
    public required string PackageRoot { get; init; }
    public required string PluginDataRoot { get; init; }
}

public static partial class DysonPluginPaths
{
    public const string ProjectPluginsRelativeDirectory = ".dyson/plugins";
    public const string ProjectPluginDataRelativeDirectory = ".dyson/plugin-data";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NormalizedPluginIdRegex();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionOrContentIdRegex();

    public static Result<string, string> NormalizePluginId(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return Result<string, string>.AsError("Plugin id is required.");

        var normalized = pluginId.Trim().ToLowerInvariant();
        if (!NormalizedPluginIdRegex().IsMatch(normalized))
        {
            return Result<string, string>.AsError(
                "Plugin id must be 1-64 lowercase letters, digits, dots, underscores, or hyphens, " +
                "and must begin and end with a letter or digit.");
        }

        return Result<string, string>.AsValue(normalized);
    }

    public static Result<DysonPluginScopeRoots, string> GetScopeRoots(DysonPluginInstallTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var validation = target.Validate();
        if (validation.IsError)
            return Result<DysonPluginScopeRoots, string>.AsError(validation.Error);

        if (target.Scope == DysonPluginInstallScope.Project)
        {
            var fs = target.WorkspaceFileSystem!;
            var plugins = fs.ResolvePath(ProjectPluginsRelativeDirectory);
            if (plugins.IsError)
                return Result<DysonPluginScopeRoots, string>.AsError(plugins.Error);

            var data = fs.ResolvePath(ProjectPluginDataRelativeDirectory);
            if (data.IsError)
                return Result<DysonPluginScopeRoots, string>.AsError(data.Error);

            return Result<DysonPluginScopeRoots, string>.AsValue(new DysonPluginScopeRoots
            {
                Scope = target.Scope,
                WorkDirectoryId = target.WorkDirectoryId,
                PluginsRoot = plugins.Value,
                PluginDataRoot = data.Value,
            });
        }

        var mode = target.AppMode!.Value;
        return Result<DysonPluginScopeRoots, string>.AsValue(new DysonPluginScopeRoots
        {
            Scope = target.Scope,
            PluginsRoot = DysonAppPaths.GetPluginsDirectory(mode),
            PluginDataRoot = DysonAppPaths.GetPluginDataDirectory(mode),
        });
    }

    public static async Task<Result<DysonPluginScopeRoots, string>> EnsureScopeRootsAsync(
        DysonPluginInstallTarget target,
        CancellationToken cancellationToken = default)
    {
        var roots = GetScopeRoots(target);
        if (roots.IsError)
            return roots;

        if (target.Scope == DysonPluginInstallScope.Project)
        {
            var fs = target.WorkspaceFileSystem!;
            var plugins = await fs.CreateDirectoryAsync(ProjectPluginsRelativeDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (plugins.IsError)
                return Result<DysonPluginScopeRoots, string>.AsError(plugins.Error);

            var data = await fs.CreateDirectoryAsync(ProjectPluginDataRelativeDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (data.IsError)
                return Result<DysonPluginScopeRoots, string>.AsError(data.Error);
        }
        else
        {
            DysonAppPaths.EnsurePluginsDirectory(target.AppMode!.Value);
            DysonAppPaths.EnsurePluginDataDirectory(target.AppMode.Value);
        }

        return roots;
    }

    public static VoidResult<string> ValidatePackageRootOwnership(
        DysonPluginInstallTarget target,
        string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(target);

        var targetValidation = target.Validate();
        if (targetValidation.IsError)
            return targetValidation;
        if (string.IsNullOrWhiteSpace(packageRoot))
            return VoidResult<string>.AsError("Plugin package root is required.");

        if (target.Scope == DysonPluginInstallScope.Project)
        {
            var relative = target.WorkspaceFileSystem!.GetRelativePath(packageRoot);
            if (relative.IsError)
                return VoidResult<string>.AsError(relative.Error);

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var prefix = ProjectPluginsRelativeDirectory + "/";
            if (!relative.Value.StartsWith(prefix, comparison))
            {
                return VoidResult<string>.AsError(
                    $"Project plugin package root must be beneath '{ProjectPluginsRelativeDirectory}'.");
            }

            return VoidResult<string>.Success;
        }

        try
        {
            var expectedRoot = Path.GetFullPath(DysonAppPaths.GetPluginsDirectory(target.AppMode!.Value));
            var actualRoot = Path.GetFullPath(packageRoot);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var prefix = expectedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            return actualRoot.StartsWith(prefix, comparison)
                ? VoidResult<string>.Success
                : VoidResult<string>.AsError(
                    $"Global plugin package root must be beneath '{expectedRoot}'.");
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Invalid plugin package root: {ex.Message}");
        }
    }

    public static Result<DysonPluginPathsResult, string> Resolve(
        DysonPluginInstallTarget target,
        string pluginId,
        string versionOrContentId)
    {
        var normalizedId = NormalizePluginId(pluginId);
        if (normalizedId.IsError)
            return Result<DysonPluginPathsResult, string>.AsError(normalizedId.Error);

        if (string.IsNullOrWhiteSpace(versionOrContentId) ||
            !VersionOrContentIdRegex().IsMatch(versionOrContentId.Trim()))
        {
            return Result<DysonPluginPathsResult, string>.AsError(
                "Plugin version/content id must be 1-128 letters, digits, dots, underscores, or hyphens, " +
                "and must begin and end with a letter or digit.");
        }

        var roots = GetScopeRoots(target);
        if (roots.IsError)
            return Result<DysonPluginPathsResult, string>.AsError(roots.Error);

        var version = versionOrContentId.Trim();
        string packageRoot;
        string dataRoot;
        if (target.Scope == DysonPluginInstallScope.Project)
        {
            var fs = target.WorkspaceFileSystem!;
            var package = fs.ResolvePath(
                $"{ProjectPluginsRelativeDirectory}/{normalizedId.Value}/{version}");
            if (package.IsError)
                return Result<DysonPluginPathsResult, string>.AsError(package.Error);

            var data = fs.ResolvePath($"{ProjectPluginDataRelativeDirectory}/{normalizedId.Value}");
            if (data.IsError)
                return Result<DysonPluginPathsResult, string>.AsError(data.Error);

            packageRoot = package.Value;
            dataRoot = data.Value;
        }
        else
        {
            packageRoot = Path.Combine(roots.Value.PluginsRoot, normalizedId.Value, version);
            dataRoot = Path.Combine(roots.Value.PluginDataRoot, normalizedId.Value);
        }

        return Result<DysonPluginPathsResult, string>.AsValue(new DysonPluginPathsResult
        {
            ScopeRoots = roots.Value,
            NormalizedPluginId = normalizedId.Value,
            VersionOrContentId = version,
            PackageRoot = packageRoot,
            PluginDataRoot = dataRoot,
        });
    }
}

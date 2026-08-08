using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

public class DysonPluginCatalogLifecycleTests
{
    [Fact]
    public async Task Effective_catalog_includes_global_only_plugin_as_active_contribution()
    {
        var packageRoot = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        try
        {
            var repository = DysonTempDb.Plugins(accessor);
            var installationId = await AddInstallationAsync(
                repository,
                "global-only",
                DysonPluginStorageValues.GlobalScope,
                Path.Combine(packageRoot, "global-only", "1"));

            var catalog = await new DysonPluginCatalogService(repository).GetEffectiveCatalogAsync(new());

            Assert.True(catalog.IsSuccess, catalog.IsError ? catalog.Error : null);
            var entry = Assert.Single(catalog.Value.Entries);
            Assert.Equal("global-only", entry.NormalizedPluginId);
            Assert.Equal(installationId, entry.EffectiveInstallation.Installation.Id);
            Assert.Null(entry.ShadowedGlobalInstallation);
            var contribution = Assert.Single(catalog.Value.ActiveContributions);
            Assert.Single(contribution.Components);
        }
        finally
        {
            TryDeleteDirectory(packageRoot);
        }
    }

    [Fact]
    public async Task Effective_catalog_excludes_project_installations_from_other_projects()
    {
        var projectA = CreateTempDirectory();
        var projectB = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        try
        {
            var workDirectories = DysonTempDb.WorkDirectories(accessor);
            var a = await workDirectories.CreateAsync(projectA, "Project A");
            var b = await workDirectories.CreateAsync(projectB, "Project B");
            Assert.True(a.IsSuccess, a.IsError ? a.Error : null);
            Assert.True(b.IsSuccess, b.IsError ? b.Error : null);

            var repository = DysonTempDb.Plugins(accessor);
            await AddInstallationAsync(
                repository,
                "project-only",
                DysonPluginStorageValues.ProjectScope,
                Path.Combine(projectA, ".dyson", "plugins", "project-only", "1"),
                a.Value);

            var catalog = await new DysonPluginCatalogService(repository).GetEffectiveCatalogAsync(new()
            {
                ActiveWorkDirectoryId = b.Value,
            });

            Assert.True(catalog.IsSuccess, catalog.IsError ? catalog.Error : null);
            Assert.Empty(catalog.Value.Entries);
            Assert.Empty(catalog.Value.ActiveContributions);
        }
        finally
        {
            TryDeleteDirectory(projectA);
            TryDeleteDirectory(projectB);
        }
    }

    [Fact]
    public async Task Project_plugin_shadows_global_even_when_project_plugin_is_disabled()
    {
        var projectRoot = CreateTempDirectory();
        var globalRoot = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        try
        {
            var workDirectories = DysonTempDb.WorkDirectories(accessor);
            var project = await workDirectories.CreateAsync(projectRoot, "Project");
            Assert.True(project.IsSuccess, project.IsError ? project.Error : null);

            var repository = DysonTempDb.Plugins(accessor);
            var globalId = await AddInstallationAsync(
                repository,
                "shared",
                DysonPluginStorageValues.GlobalScope,
                Path.Combine(globalRoot, "shared", "1"));
            var projectId = await AddInstallationAsync(
                repository,
                "shared",
                DysonPluginStorageValues.ProjectScope,
                Path.Combine(projectRoot, ".dyson", "plugins", "shared", "2"),
                project.Value,
                isEnabled: false,
                status: "Disabled");

            var catalog = await new DysonPluginCatalogService(repository).GetEffectiveCatalogAsync(new()
            {
                ActiveWorkDirectoryId = project.Value,
            });

            Assert.True(catalog.IsSuccess, catalog.IsError ? catalog.Error : null);
            var entry = Assert.Single(catalog.Value.Entries);
            Assert.Equal(projectId, entry.EffectiveInstallation.Installation.Id);
            Assert.Equal(globalId, entry.ShadowedGlobalInstallation!.Installation.Id);
            Assert.Empty(catalog.Value.ActiveContributions);
        }
        finally
        {
            TryDeleteDirectory(projectRoot);
            TryDeleteDirectory(globalRoot);
        }
    }

    [Fact]
    public async Task Disabled_and_non_ready_statuses_do_not_contribute_and_are_inspectable()
    {
        var root = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        try
        {
            var repository = DysonTempDb.Plugins(accessor);
            var disabledId = await AddInstallationAsync(
                repository,
                "disabled",
                DysonPluginStorageValues.GlobalScope,
                Path.Combine(root, "disabled", "1"),
                isEnabled: false,
                status: "Disabled");
            await AddInstallationAsync(
                repository,
                "invalid",
                DysonPluginStorageValues.GlobalScope,
                Path.Combine(root, "invalid", "1"),
                status: "Invalid");

            var service = new DysonPluginCatalogService(repository);
            var catalog = await service.GetEffectiveCatalogAsync(new());
            var status = await service.GetStatusAsync(disabledId);
            var diagnostics = await service.GetDiagnosticsAsync(disabledId);

            Assert.True(catalog.IsSuccess, catalog.IsError ? catalog.Error : null);
            Assert.Empty(catalog.Value.ActiveContributions);
            Assert.True(status.IsSuccess, status.IsError ? status.Error : null);
            Assert.Equal(DysonPluginStatus.Disabled, status.Value);
            Assert.True(diagnostics.IsSuccess, diagnostics.IsError ? diagnostics.Error : null);
            Assert.Contains(diagnostics.Value, diagnostic => diagnostic.Code == "fixture-warning");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Uninstall_rejects_package_root_that_is_not_one_owned_version_directory()
    {
        var projectRoot = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        try
        {
            var (projectId, target) = await CreateProjectTargetAsync(accessor, projectRoot);
            var repository = DysonTempDb.Plugins(accessor);
            var unsafeRoot = Path.Combine(projectRoot, ".dyson", "plugins", "unsafe");
            Directory.CreateDirectory(unsafeRoot);
            var installationId = await AddInstallationAsync(
                repository,
                "unsafe",
                DysonPluginStorageValues.ProjectScope,
                unsafeRoot,
                projectId);

            var result = await new DysonPluginLifecycleService(repository).UninstallAsync(new()
            {
                InstallationId = installationId,
                Target = target,
                PluginDataDisposition = DysonPluginDataDisposition.Delete,
            });

            Assert.True(result.IsError);
            Assert.Contains("exactly one", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(unsafeRoot));
            Assert.True((await repository.GetAsync(installationId)).IsSuccess);
        }
        finally
        {
            TryDeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task Lifecycle_enablement_raises_scope_aware_change_events()
    {
        var projectRoot = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        try
        {
            var (projectId, _) = await CreateProjectTargetAsync(accessor, projectRoot);
            var repository = DysonTempDb.Plugins(accessor);
            var installationId = await AddInstallationAsync(
                repository,
                "events",
                DysonPluginStorageValues.ProjectScope,
                Path.Combine(projectRoot, ".dyson", "plugins", "events", "1"),
                projectId);
            var service = new DysonPluginLifecycleService(repository);
            var changes = new List<DysonPluginCatalogChangedEventArgs>();
            service.Changed += (_, change) => changes.Add(change);

            var installed = await service.NotifyInstalledAsync(installationId);
            var disabled = await service.SetEnabledAsync(installationId, false);
            var enabled = await service.SetEnabledAsync(installationId, true);

            Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
            Assert.True(disabled.IsSuccess, disabled.IsError ? disabled.Error : null);
            Assert.True(enabled.IsSuccess, enabled.IsError ? enabled.Error : null);
            Assert.Collection(changes,
                change =>
                {
                    Assert.Equal(DysonPluginCatalogChangeKind.Installed, change.Kind);
                    Assert.Equal(projectId, change.WorkDirectoryId);
                    Assert.Equal(DysonPluginInstallScope.Project, change.Scope);
                },
                change => Assert.Equal(DysonPluginCatalogChangeKind.Disabled, change.Kind),
                change => Assert.Equal(DysonPluginCatalogChangeKind.Enabled, change.Kind));
        }
        finally
        {
            TryDeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task Uninstall_deletes_package_and_honors_plugin_data_retain_or_delete_choice()
    {
        var projectRoot = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        try
        {
            var (projectId, target) = await CreateProjectTargetAsync(accessor, projectRoot);
            var repository = DysonTempDb.Plugins(accessor);
            var service = new DysonPluginLifecycleService(repository);

            var retainedPackage = Path.Combine(projectRoot, ".dyson", "plugins", "retained", "1");
            var retainedData = Path.Combine(projectRoot, ".dyson", "plugin-data", "retained");
            Directory.CreateDirectory(retainedPackage);
            Directory.CreateDirectory(retainedData);
            File.WriteAllText(Path.Combine(retainedPackage, "plugin.json"), "{}");
            File.WriteAllText(Path.Combine(retainedData, "state.json"), "{}");
            var retainedId = await AddInstallationAsync(
                repository,
                "retained",
                DysonPluginStorageValues.ProjectScope,
                retainedPackage,
                projectId);

            var retain = await service.UninstallAsync(new()
            {
                InstallationId = retainedId,
                Target = target,
                PluginDataDisposition = DysonPluginDataDisposition.Retain,
            });

            Assert.True(retain.IsSuccess, retain.IsError ? retain.Error : null);
            Assert.True(retain.Value.PackageDeleted);
            Assert.False(retain.Value.PluginDataDeleted);
            Assert.False(Directory.Exists(retainedPackage));
            Assert.True(Directory.Exists(retainedData));
            Assert.True((await repository.GetAsync(retainedId)).IsError);

            var deletedPackage = Path.Combine(projectRoot, ".dyson", "plugins", "deleted", "1");
            var deletedData = Path.Combine(projectRoot, ".dyson", "plugin-data", "deleted");
            Directory.CreateDirectory(deletedPackage);
            Directory.CreateDirectory(deletedData);
            var deletedId = await AddInstallationAsync(
                repository,
                "deleted",
                DysonPluginStorageValues.ProjectScope,
                deletedPackage,
                projectId);

            var delete = await service.UninstallAsync(new()
            {
                InstallationId = deletedId,
                Target = target,
                PluginDataDisposition = DysonPluginDataDisposition.Delete,
            });

            Assert.True(delete.IsSuccess, delete.IsError ? delete.Error : null);
            Assert.True(delete.Value.PackageDeleted);
            Assert.True(delete.Value.PluginDataDeleted);
            Assert.False(Directory.Exists(deletedPackage));
            Assert.False(Directory.Exists(deletedData));
        }
        finally
        {
            TryDeleteDirectory(projectRoot);
        }
    }

    private static async Task<(Guid WorkDirectoryId, DysonPluginInstallTarget Target)> CreateProjectTargetAsync(
        DysonDbAccessor accessor,
        string projectRoot)
    {
        var workDirectory = await DysonTempDb.WorkDirectories(accessor)
            .CreateAsync(projectRoot, "Project");
        Assert.True(workDirectory.IsSuccess, workDirectory.IsError ? workDirectory.Error : null);

        var fileSystem = await DysonWorkspaceFileSystems.CreateLocalAsync(projectRoot);
        Assert.True(fileSystem.IsSuccess, fileSystem.IsError ? fileSystem.Error : null);
        var target = DysonPluginInstallTarget.ForProject(workDirectory.Value, fileSystem.Value);
        Assert.True(target.IsSuccess, target.IsError ? target.Error : null);
        return (workDirectory.Value, target.Value);
    }

    private static async Task<Guid> AddInstallationAsync(
        IDysonPluginInstallationRepository repository,
        string pluginId,
        string scope,
        string packageRoot,
        Guid? workDirectoryId = null,
        bool isEnabled = true,
        string status = "Installed")
    {
        var created = await repository.UpsertAsync(new DysonPluginInstallationEntity
        {
            NormalizedPluginId = pluginId,
            DisplayName = pluginId,
            Version = "1",
            SourceKind = "LocalFolder",
            SourceLocation = packageRoot,
            PackageFormat = "AgentPlugin",
            InstallScope = scope,
            WorkDirectoryId = workDirectoryId,
            IsEnabled = isEnabled,
            Status = status,
            PackageRoot = Path.GetFullPath(packageRoot),
            ComponentInventoryJson = JsonSerializer.Serialize(new[]
            {
                new DysonResolvedPluginComponent
                {
                    Id = "skill",
                    Kind = DysonPluginComponentKind.Skill,
                    RelativePath = "skills/skill/SKILL.md",
                },
            }),
            DiagnosticsJson = JsonSerializer.Serialize(new[]
            {
                new DysonPluginDiagnostic
                {
                    Severity = DysonPluginDiagnosticSeverity.Warning,
                    Code = "fixture-warning",
                    Message = "fixture diagnostic",
                },
            }),
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        return created.Value;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dyson-plugin-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}

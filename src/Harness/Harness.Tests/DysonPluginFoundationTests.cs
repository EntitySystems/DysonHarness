using DysonHarness;

namespace Harness.Tests;

public class DysonPluginFoundationTests
{
    [Fact]
    public async Task Project_target_requires_initialized_workspace_and_forms_owned_paths()
    {
        var root = CreateTempDirectory();
        try
        {
            var uninitialized = new DysonLocalWorkspaceFileSystem(root);
            var rejected = DysonPluginInstallTarget.ForProject(Guid.NewGuid(), uninitialized);
            Assert.True(rejected.IsError);
            Assert.Contains("initialized", rejected.Error, StringComparison.OrdinalIgnoreCase);

            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            var workDirectoryId = Guid.NewGuid();
            var target = DysonPluginInstallTarget.ForProject(workDirectoryId, created.Value);
            Assert.True(target.IsSuccess, target.IsError ? target.Error : null);

            var resolved = DysonPluginPaths.Resolve(target.Value, "Example.Plugin", "v1_2");
            Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
            Assert.Equal("example.plugin", resolved.Value.NormalizedPluginId);
            Assert.Equal(workDirectoryId, resolved.Value.ScopeRoots.WorkDirectoryId);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, ".dyson", "plugins", "example.plugin", "v1_2")),
                resolved.Value.PackageRoot);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, ".dyson", "plugin-data", "example.plugin")),
                resolved.Value.PluginDataRoot);

            var ensured = await DysonPluginPaths.EnsureScopeRootsAsync(target.Value);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            Assert.True(Directory.Exists(Path.Combine(root, ".dyson", "plugins")));
            Assert.True(Directory.Exists(Path.Combine(root, ".dyson", "plugin-data")));

            Assert.True(DysonPluginPaths.Resolve(target.Value, "../escape", "1").IsError);
            Assert.True(DysonPluginPaths.Resolve(target.Value, "valid", "../escape").IsError);
            Assert.True(DysonPluginPaths.ValidatePackageRootOwnership(
                target.Value,
                resolved.Value.PackageRoot).IsSuccess);
            Assert.True(DysonPluginPaths.ValidatePackageRootOwnership(
                target.Value,
                Path.Combine(root, ".dyson", "plugin-data", "example.plugin")).IsError);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Global_target_uses_mode_scoped_package_and_data_roots()
    {
        var target = DysonPluginInstallTarget.ForGlobal(DysonAppMode.Test);
        Assert.True(target.IsSuccess, target.IsError ? target.Error : null);

        var roots = DysonPluginPaths.GetScopeRoots(target.Value);
        Assert.True(roots.IsSuccess, roots.IsError ? roots.Error : null);
        Assert.Equal(DysonAppPaths.GetPluginsDirectory(DysonAppMode.Test), roots.Value.PluginsRoot);
        Assert.Equal(DysonAppPaths.GetPluginDataDirectory(DysonAppMode.Test), roots.Value.PluginDataRoot);
        Assert.EndsWith(Path.Combine("DysonTest", "plugins"), roots.Value.PluginsRoot);
        Assert.EndsWith(Path.Combine("DysonTest", "plugin-data"), roots.Value.PluginDataRoot);

        var resolved = DysonPluginPaths.Resolve(target.Value, "global-plugin", "sha256_abc");
        Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
        Assert.True(DysonPluginPaths.ValidatePackageRootOwnership(
            target.Value,
            resolved.Value.PackageRoot).IsSuccess);
        Assert.True(DysonPluginPaths.ValidatePackageRootOwnership(
            target.Value,
            Path.Combine(DysonAppPaths.GetPluginDataDirectory(DysonAppMode.Test), "global-plugin")).IsError);
    }

    [Fact]
    public void Preview_and_install_validation_returns_expected_failures()
    {
        var invalidParse = DysonPluginRequestValidation.Validate(new DysonPluginParseRequest
        {
            StagedPackageRoot = "relative/path",
            Source = new DysonPluginSource
            {
                Kind = DysonPluginSourceKind.LocalFolder,
                Location = "folder",
            },
        });
        Assert.True(invalidParse.IsError);

        var invalidPreview = DysonPluginRequestValidation.Validate(new DysonPluginPreviewRequest
        {
            SourceKind = DysonPluginSourceKind.LocalFolder,
            SourceLocation = " ",
        });
        Assert.True(invalidPreview.IsError);

        var invalidLocalRef = DysonPluginRequestValidation.Validate(new DysonPluginPreviewRequest
        {
            SourceKind = DysonPluginSourceKind.LocalZip,
            SourceLocation = "plugin.zip",
            RequestedRef = "main",
        });
        Assert.True(invalidLocalRef.IsError);

        var validZip = DysonPluginRequestValidation.Validate(new DysonPluginPreviewRequest
        {
            SourceKind = DysonPluginSourceKind.LocalZip,
            SourceLocation = "plugin.zip",
            ArchiveBytes = new byte[] { 1, 2, 3 },
        });
        Assert.True(validZip.IsSuccess, validZip.IsError ? validZip.Error : null);

        var invalidInstall = DysonPluginRequestValidation.Validate(new DysonPluginInstallRequest
        {
            PreviewId = Guid.Empty,
            Target = DysonPluginInstallTarget.ForGlobal(DysonAppMode.Dev).Value,
        });
        Assert.True(invalidInstall.IsError);
    }

    [Fact]
    public async Task Repository_enforces_scope_ownership_and_effective_listing()
    {
        var root = CreateTempDirectory();
        var globalRoot = CreateTempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        var subject = DysonTempDb.Subject("subject-a");
        var workDirectories = DysonTempDb.WorkDirectories(accessor, subject);
        var plugins = DysonTempDb.Plugins(accessor, subject);

        try
        {
            var workDirectory = await workDirectories.CreateAsync(root, "Project A");
            Assert.True(workDirectory.IsSuccess, workDirectory.IsError ? workDirectory.Error : null);

            var global = CreateInstallation(
                "sample",
                DysonPluginStorageValues.GlobalScope,
                Path.Combine(globalRoot, "sample", "1"));
            var globalCreated = await plugins.UpsertAsync(global);
            Assert.True(globalCreated.IsSuccess, globalCreated.IsError ? globalCreated.Error : null);

            var project = CreateInstallation(
                "sample",
                DysonPluginStorageValues.ProjectScope,
                Path.Combine(root, ".dyson", "plugins", "sample", "1"),
                workDirectory.Value);
            var projectCreated = await plugins.UpsertAsync(project);
            Assert.True(projectCreated.IsSuccess, projectCreated.IsError ? projectCreated.Error : null);
            Assert.NotEqual(globalCreated.Value, projectCreated.Value);

            var globals = await plugins.ListAsync();
            Assert.True(globals.IsSuccess, globals.IsError ? globals.Error : null);
            Assert.Single(globals.Value);
            Assert.Equal(DysonPluginStorageValues.GlobalScope, globals.Value[0].InstallScope);

            var effective = await plugins.ListAsync(workDirectory.Value);
            Assert.True(effective.IsSuccess, effective.IsError ? effective.Error : null);
            Assert.Equal(2, effective.Value.Count);

            project.Version = "2";
            project.PackageRoot = Path.Combine(root, ".dyson", "plugins", "sample", "2");
            var updated = await plugins.UpsertAsync(project);
            Assert.True(updated.IsSuccess, updated.IsError ? updated.Error : null);
            Assert.Equal(projectCreated.Value, updated.Value);

            var loaded = await plugins.GetAsync(projectCreated.Value);
            Assert.True(loaded.IsSuccess, loaded.IsError ? loaded.Error : null);
            Assert.Equal("2", loaded.Value.Version);
            Assert.Equal("packages/sample", loaded.Value.SourceSubdirectory);
            Assert.Equal(DateTimeKind.Utc, loaded.Value.InstalledUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, loaded.Value.UpdatedUtc.Kind);

            var disabled = await plugins.SetEnabledAsync(projectCreated.Value, false);
            Assert.True(disabled.IsSuccess, disabled.IsError ? disabled.Error : null);
            loaded = await plugins.GetAsync(projectCreated.Value);
            Assert.False(loaded.Value.IsEnabled);
            Assert.Equal("Disabled", loaded.Value.Status);

            var mismatchedOwner = CreateInstallation(
                "other",
                DysonPluginStorageValues.ProjectScope,
                Path.Combine(globalRoot, "other", "1"),
                workDirectory.Value);
            var mismatch = await plugins.UpsertAsync(mismatchedOwner);
            Assert.True(mismatch.IsError);
            Assert.Contains("owned", mismatch.Error, StringComparison.OrdinalIgnoreCase);

            var invalidGlobal = CreateInstallation(
                "other",
                DysonPluginStorageValues.GlobalScope,
                Path.Combine(globalRoot, "other", "1"),
                workDirectory.Value);
            var invalidGlobalResult = await plugins.UpsertAsync(invalidGlobal);
            Assert.True(invalidGlobalResult.IsError);
            Assert.Contains("must not", invalidGlobalResult.Error, StringComparison.OrdinalIgnoreCase);

            subject.SubjectId = "subject-b";
            var crossSubjectGet = await plugins.GetAsync(projectCreated.Value);
            Assert.True(crossSubjectGet.IsError);
            Assert.Contains("not found", crossSubjectGet.Error, StringComparison.OrdinalIgnoreCase);

            var crossSubjectList = await plugins.ListAsync(workDirectory.Value);
            Assert.True(crossSubjectList.IsError);
            Assert.Contains("current subject", crossSubjectList.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(globalRoot);
        }
    }

    [Fact]
    public async Task Migration_creates_durable_plugin_installation_table()
    {
        var globalRoot = CreateTempDirectory();
        var (accessor, databasePath) = DysonTempDb.OpenFileAccessor();
        try
        {
            var plugins = DysonTempDb.Plugins(accessor);
            var created = await plugins.UpsertAsync(CreateInstallation(
                "migrated",
                DysonPluginStorageValues.GlobalScope,
                Path.Combine(globalRoot, "migrated", "1")));
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            var listed = await plugins.ListAsync();
            Assert.True(listed.IsSuccess, listed.IsError ? listed.Error : null);
            Assert.Single(listed.Value);
        }
        finally
        {
            TryDeleteFile(databasePath);
            TryDeleteFile(databasePath + "-wal");
            TryDeleteFile(databasePath + "-shm");
            TryDeleteDirectory(globalRoot);
        }
    }

    private static DysonPluginInstallationEntity CreateInstallation(
        string normalizedId,
        string scope,
        string packageRoot,
        Guid? workDirectoryId = null) =>
        new()
        {
            NormalizedPluginId = normalizedId,
            DisplayName = normalizedId,
            Version = "1",
            SourceKind = "LocalFolder",
            SourceLocation = packageRoot,
            SourceSubdirectory = "packages/sample",
            ContentChecksum = "sha256:test",
            PackageFormat = "AgentPlugin",
            SchemaVersion = "1.0.0",
            InstallScope = scope,
            WorkDirectoryId = workDirectoryId,
            IsEnabled = true,
            Status = "Installed",
            PackageRoot = Path.GetFullPath(packageRoot),
            ComponentInventoryJson = "[]",
            ConfigurationSchemaJson = "{}",
            DiagnosticsJson = "[]",
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dyson-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

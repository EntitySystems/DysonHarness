using DysonHarness;
using Harness.UI.Components.Plugins;

namespace Harness.Tests;

public class PluginImportControllerTests
{
    [Fact]
    public void Source_selection_is_mutually_exclusive_and_zip_quota_is_enforced()
    {
        var flow = new PluginImportController();

        Assert.True(flow.SelectFolder("C:\\plugins\\sample").IsSuccess);
        Assert.Equal(DysonPluginSourceKind.LocalFolder, flow.SourceKind);
        Assert.NotNull(flow.FolderPath);

        flow.SelectGitHub();
        flow.Repository = "owner/repository";
        Assert.Equal(DysonPluginSourceKind.GitHub, flow.SourceKind);
        Assert.Null(flow.FolderPath);

        var oversize = flow.SelectZip("sample.zip", declaredLength: 5, bytes: [1, 2, 3, 4, 5], maxArchiveBytes: 4);
        Assert.True(oversize.IsError);
        Assert.Equal(DysonPluginSourceKind.GitHub, flow.SourceKind);

        var zip = flow.SelectZip("sample.zip", declaredLength: 3, bytes: [1, 2, 3], maxArchiveBytes: 4);
        Assert.True(zip.IsSuccess, zip.IsError ? zip.Error : null);
        Assert.Equal(DysonPluginSourceKind.LocalZip, flow.SourceKind);
        Assert.Null(flow.FolderPath);
        Assert.Equal("", flow.Repository);
        Assert.Equal([1, 2, 3], flow.ZipBytes);
    }

    [Fact]
    public void Preview_requires_an_explicit_scope_and_is_retained_after_scope_cancellation()
    {
        var flow = new PluginImportController();
        flow.SelectGitHub();
        flow.Repository = "owner/repository";
        var preview = Preview();

        flow.ApplyPreview(preview);

        Assert.Equal(PluginImportPhase.Scope, flow.Phase);
        Assert.Null(flow.SelectedTarget);
        Assert.False(flow.CanInstall);

        var global = DysonPluginInstallTarget.ForGlobal(DysonAppMode.Test);
        Assert.True(global.IsSuccess, global.IsError ? global.Error : null);
        Assert.True(flow.SelectScope(global.Value).IsSuccess);
        Assert.Equal(PluginImportPhase.Confirmation, flow.Phase);
        Assert.Equal(global.Value, flow.SelectedTarget);

        flow.CancelScopeSelection();

        Assert.Equal(PluginImportPhase.Scope, flow.Phase);
        Assert.Equal(preview.PreviewId, flow.Preview?.PreviewId);
        Assert.Null(flow.SelectedTarget);
        Assert.False(flow.ConfirmationAccepted);
        Assert.False(flow.CanInstall);
    }

    [Fact]
    public void No_project_target_or_scope_is_defaulted_by_preview()
    {
        var flow = new PluginImportController();
        flow.ApplyPreview(Preview());

        Assert.Null(flow.SelectedTarget);
        Assert.False(flow.CanInstall);
        Assert.Equal(PluginImportPhase.Scope, flow.Phase);
    }

    [Fact]
    public void Selected_scope_forms_service_target_and_confirmation_gates_install()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-plugin-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var workDirectoryId = Guid.NewGuid();
            var target = DysonPluginInstallTarget.ForProject(workDirectoryId, fs);
            Assert.True(target.IsSuccess, target.IsError ? target.Error : null);

            var flow = new PluginImportController();
            flow.ApplyPreview(Preview());
            Assert.True(flow.SelectScope(target.Value).IsSuccess);
            Assert.Equal(DysonPluginInstallScope.Project, flow.SelectedTarget?.Scope);
            Assert.Equal(workDirectoryId, flow.SelectedTarget?.WorkDirectoryId);
            Assert.False(flow.CanInstall);

            flow.SetConfirmationAccepted(true);
            Assert.True(flow.CanInstall);
            Assert.True(flow.BeginInstall().IsSuccess);
            Assert.Equal(PluginImportPhase.Installing, flow.Phase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Capability_warnings_cover_all_confirmation_risks()
    {
        var flow = new PluginImportController();
        flow.ApplyPreview(Preview(
            capabilities: DysonPluginCapabilities.Hooks
                | DysonPluginCapabilities.McpExecutable
                | DysonPluginCapabilities.McpNetwork
                | DysonPluginCapabilities.Variables
                | DysonPluginCapabilities.UnsupportedComponents,
            components:
            [
                new DysonResolvedPluginComponent
                {
                    Id = "legacy-app",
                    Kind = DysonPluginComponentKind.Unsupported,
                    RelativePath = ".app.json",
                    IsSupported = false,
                },
            ]));

        var warnings = flow.GetCapabilityWarnings();
        Assert.Equal(5, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("Hooks", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("execute", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.Contains("network", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.Contains("variables", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Committed_install_with_notification_warning_is_success_until_acknowledged_then_resets()
    {
        var flow = new PluginImportController();
        flow.ApplyPreview(Preview());
        var target = DysonPluginInstallTarget.ForGlobal(DysonAppMode.Test).Value;
        Assert.True(flow.SelectScope(target).IsSuccess);
        flow.SetConfirmationAccepted(true);
        Assert.True(flow.BeginInstall().IsSuccess);

        var installed = InstallResult();
        flow.CompleteInstall(installed, "Catalog notification failed.");

        Assert.Equal(PluginImportPhase.Success, flow.Phase);
        Assert.Equal(installed.InstallationId, flow.InstallResult?.InstallationId);
        Assert.Equal("Catalog notification failed.", flow.RefreshWarning);

        flow.AcknowledgeSuccess();

        Assert.Equal(PluginImportPhase.Source, flow.Phase);
        Assert.Null(flow.Preview);
        Assert.Null(flow.InstallResult);
        Assert.Null(flow.SelectedTarget);
        Assert.Null(flow.RefreshWarning);
    }

    [Fact]
    public void Cursor_ambiguity_parser_exposes_only_actionable_package_paths()
    {
        var paths = PluginImportController.ParseAmbiguousPackagePaths(
            "Cursor marketplace contains multiple plugin packages. Select a plugin subdirectory: Alpha (packages/alpha), Beta (packages/beta).");

        Assert.Equal(["packages/alpha", "packages/beta"], paths);
        Assert.Empty(PluginImportController.ParseAmbiguousPackagePaths("No package manifest was found."));
    }

    private static DysonPluginPreview Preview(
        DysonPluginCapabilities capabilities = DysonPluginCapabilities.None,
        IReadOnlyList<DysonResolvedPluginComponent>? components = null) => new()
    {
        PreviewId = Guid.NewGuid(),
        StagedPackageRoot = Path.GetTempPath(),
        CreatedUtc = DateTime.UtcNow,
        Plugin = new DysonResolvedPlugin
        {
            Format = DysonPluginPackageFormat.Cursor,
            Manifest = new DysonPluginManifestMetadata
            {
                NormalizedId = "sample-plugin",
                DisplayName = "Sample plugin",
                Version = "1.0.0",
            },
            Source = new DysonPluginSource
            {
                Kind = DysonPluginSourceKind.GitHub,
                Location = "owner/repository",
                ResolvedCommit = "0123456789012345678901234567890123456789",
                ContentChecksum = "sha256:012345678901234567890123456789012345678901234567890123456789abcdef",
            },
            Capabilities = capabilities,
            Components = components ?? [],
        },
    };

    private static DysonPluginInstallResult InstallResult() => new()
    {
        InstallationId = Guid.NewGuid(),
        Scope = DysonPluginInstallScope.Global,
        PackageRoot = Path.Combine(Path.GetTempPath(), "plugins", "sample-plugin", "1.0.0"),
        PluginDataRoot = Path.Combine(Path.GetTempPath(), "plugin-data", "sample-plugin"),
        InstalledUtc = DateTime.UtcNow,
        Plugin = Preview().Plugin,
    };
}

using DysonHarness;
using Harness.UI.Components.Plugins;

namespace Harness.Tests;

public sealed class PluginManagementControllerTests
{
    [Fact]
    public void Flatten_keeps_effective_project_and_shadowed_global_installations_visible()
    {
        var project = Installation("shared", DysonPluginStorageValues.ProjectScope);
        var global = Installation("shared", DysonPluginStorageValues.GlobalScope);
        var catalog = new DysonEffectivePluginCatalog
        {
            Entries =
            [
                new DysonEffectivePluginCatalogEntry
                {
                    NormalizedPluginId = "shared",
                    EffectiveInstallation = project,
                    ShadowedGlobalInstallations = [global],
                },
            ],
        };

        var items = PluginManagementController.Flatten(catalog);

        Assert.Collection(items,
            item =>
            {
                Assert.True(item.IsEffective);
                Assert.False(item.IsShadowedGlobal);
                Assert.Equal(DysonPluginStorageValues.ProjectScope, item.Installation.Installation.InstallScope);
            },
            item =>
            {
                Assert.False(item.IsEffective);
                Assert.True(item.IsShadowedGlobal);
                Assert.Equal(DysonPluginStorageValues.GlobalScope, item.Installation.Installation.InstallScope);
            });
    }

    [Fact]
    public void Hook_grant_builder_allows_only_supported_review_values()
    {
        var installationId = Guid.NewGuid();

        var invalid = PluginManagementController.BuildHookGrant(
            installationId, "audit", "unknown", [DysonPluginHookPermissions.ReadToolMetadata],
            DysonPluginHookFailureMode.FailOpen, 1_000, 4_096);
        var valid = PluginManagementController.BuildHookGrant(
            installationId, "audit", DysonPluginHookEvents.ToolBefore,
            [DysonPluginHookPermissions.GateTool, DysonPluginHookPermissions.ReadToolMetadata, DysonPluginHookPermissions.GateTool],
            DysonPluginHookFailureMode.FailClosed, 1_000, 4_096);

        Assert.True(invalid.IsError);
        Assert.True(valid.IsSuccess, valid.IsError ? valid.Error : null);
        Assert.Equal([DysonPluginHookPermissions.GateTool, DysonPluginHookPermissions.ReadToolMetadata], valid.Value.Permissions);
        Assert.Equal(DysonPluginHookFailureMode.FailClosed, valid.Value.FailureMode);
    }

    private static DysonPluginCatalogInstallation Installation(string id, string scope) => new()
    {
        Installation = new DysonPluginInstallationEntity
        {
            Id = Guid.NewGuid(),
            NormalizedPluginId = id,
            DisplayName = id,
            SourceKind = "LocalFolder",
            SourceLocation = "fixture",
            PackageFormat = "Cursor",
            InstallScope = scope,
            Status = "Installed",
            PackageRoot = Path.Combine(Path.GetTempPath(), id),
        },
        Status = DysonPluginStatus.Installed,
        Components = [],
        Diagnostics = [],
    };
}

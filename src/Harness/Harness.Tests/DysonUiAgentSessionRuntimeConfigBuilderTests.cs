using System.Text.Json;
using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: retained-scope builder matches host non-UI session config for a root demo session.
/// Theme is an input snapshot; browser/circuit services are never captured.
/// </summary>
public class DysonUiAgentSessionRuntimeConfigBuilderTests
{
    [Fact]
    public async Task BuildAsync_uses_supplied_theme_and_leaves_browser_null()
    {
        await using var harness = await Harness.CreateAsync();
        var theme = new DysonUiThemeSnapshot("light", "#9b7aef");

        await using var lease = (await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            Theme = theme,
            AgentMode = DysonAgentModes.Work,
        })).Value;

        Assert.Null(lease.Config.BrowserControl);
        Assert.Equal(theme.Theme, lease.Config.UiTheme.Theme);
        Assert.Equal(theme.AccentHex, lease.Config.UiTheme.AccentHex);
        Assert.Same(theme, lease.Config.UiTheme);
    }

    [Fact]
    public async Task BuildAsync_loads_enabled_shells()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Shells.CreateAsync("ConfigBuilderShell", "cmd.exe");
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        await using var lease = (await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            AgentMode = DysonAgentModes.Work,
        })).Value;

        Assert.Contains(lease.Config.AvailableShells, spec => spec.Name == "ConfigBuilderShell");
    }

    [Fact]
    public async Task BuildAsync_applies_mode_tool_policy()
    {
        await using var harness = await Harness.CreateAsync();
        var policy = new DysonToolPolicyStore(harness.Settings);
        var saved = await policy.SetModeDisabledToolsAsync(DysonAgentModes.Work, ["WriteFile"]);
        Assert.False(saved.IsError, saved.IsError ? saved.Error : null);

        await using var lease = (await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            AgentMode = DysonAgentModes.Work,
        })).Value;

        Assert.NotNull(lease.Config.ToolPolicy);
        Assert.NotNull(lease.Config.DisabledTools);
        Assert.Contains("WriteFile", lease.Config.DisabledTools);
    }

    [Fact]
    public async Task BuildAsync_retains_custom_mcp_and_respects_mcp_active()
    {
        await using var harness = await Harness.CreateAsync();
        var work = await harness.SeedWorkDirectoryAsync();
        var upsert = await harness.WorkDirectoryConfigurations.UpsertAsync(
            work.WorkDirectoryId,
            DysonWorkDirectoryConfig.WithMcpActive(null, false));
        Assert.False(upsert.IsError, upsert.IsError ? upsert.Error : null);

        await using var lease = (await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = work.WorkDirectoryId,
        })).Value;

        Assert.NotNull(lease.Config.CustomMcpHost);
        Assert.Equal(work.WorkDirectoryId, lease.Config.CustomMcpHost.WorkDirectoryId);
        Assert.False(lease.Config.CustomMcpHost.McpActive);
        Assert.Equal(work.WorkDirectoryId, lease.Config.PluginMcpWorkDirectoryId);
        Assert.NotNull(lease.Config.PluginMcpHost);
        Assert.True(DysonCustomMcpHostRegistry.TryGet(work.WorkDirectoryId, out _));
    }

    [Fact]
    public async Task BuildAsync_missing_work_directory_returns_error()
    {
        await using var harness = await Harness.CreateAsync();
        var missing = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var built = await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = missing,
        });

        Assert.True(built.IsError);
        Assert.Contains("not found", built.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildAsync_merges_plugin_custom_agents()
    {
        await using var harness = await Harness.CreateAsync();
        var work = await harness.SeedWorkDirectoryAsync();
        const string packageRelative = ".dyson/plugins/reviewer/1";
        using var plugin = new PluginPackage("reviewer", Path.Combine(work.WorkRoot, packageRelative.Replace('/', Path.DirectorySeparatorChar)));
        plugin.Write("agents/review.md", "---\nname: Reviewer\n---\nReview changes carefully.");
        await harness.AddPluginAgentAsync(plugin, work.WorkDirectoryId, "review", "agents/review.md");

        await using var lease = (await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = work.WorkDirectoryId,
        })).Value;

        Assert.Equal("Review changes carefully.", lease.Config.CustomAgents["reviewer:review"]);
        Assert.Contains(lease.Config.PluginContributions.Agents, agent => agent.StableId == "reviewer:review");
    }

    [Fact]
    public async Task BuildAsync_hydrates_openai_settings_and_ignores_demo_slugs()
    {
        await using var harness = await Harness.CreateAsync();
        var catalog = await harness.SeedProvidersAsync();
        var summarizer = await harness.Settings.SetSettingAsync(
            DysonAppSettingKeys.WebSearchSummarizerModelSlugId, catalog.OpenAiSlugId.ToString("D"));
        Assert.False(summarizer.IsError, summarizer.IsError ? summarizer.Error : null);
        var explore = await harness.Settings.SetSettingAsync(
            DysonAppSettingKeys.ExploreModelSlugId, catalog.OpenAiSlugId.ToString("D"));
        Assert.False(explore.IsError, explore.IsError ? explore.Error : null);
        var drone = await harness.Settings.SetSettingAsync(
            DysonAppSettingKeys.DroneModelSlugId, catalog.DemoSlugId.ToString("D"));
        Assert.False(drone.IsError, drone.IsError ? drone.Error : null);

        await using var lease = (await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            AgentMode = DysonAgentModes.Work,
        })).Value;

        var summarizerProvider = Assert.IsType<OpenAiCompatibleAgentProvider>(lease.Config.SummarizerProvider);
        Assert.Equal(catalog.OpenAiSlugId, summarizerProvider.SlugId);
        var exploreProvider = Assert.IsType<OpenAiCompatibleAgentProvider>(lease.Config.ExploreDefaultProvider);
        Assert.Equal(catalog.OpenAiSlugId, exploreProvider.SlugId);
        Assert.Null(lease.Config.DroneDefaultProvider);
        Assert.Null(lease.Config.TurnSummarizerProvider);
    }

    [Fact]
    public async Task DisposeAsync_releases_custom_mcp_retain()
    {
        await using var harness = await Harness.CreateAsync();
        var work = await harness.SeedWorkDirectoryAsync();

        var lease = (await harness.Builder.BuildAsync(new DysonUiAgentSessionRuntimeConfigRequest
        {
            WorkDirectoryId = work.WorkDirectoryId,
        })).Value;
        Assert.True(DysonCustomMcpHostRegistry.TryGet(work.WorkDirectoryId, out _));

        await lease.DisposeAsync();

        Assert.False(DysonCustomMcpHostRegistry.TryGet(work.WorkDirectoryId, out _));
        Assert.Null(lease.Config.CustomMcpHost);
        Assert.Null(lease.Config.PluginMcpHost);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _workRoot;

        private Harness(
            SqliteConnection connection,
            DysonUiAgentSessionRuntimeConfigBuilder builder,
            IDysonWorkDirectoryRepository workDirectories,
            IDysonWorkDirectoryConfigurationRepository workDirectoryConfigurations,
            IDysonSubjectSettingsRepository settings,
            IDysonConfiguredShellRepository shells,
            IDysonModelRepository models,
            IDysonPluginInstallationRepository plugins,
            string workRoot)
        {
            _connection = connection;
            Builder = builder;
            WorkDirectories = workDirectories;
            WorkDirectoryConfigurations = workDirectoryConfigurations;
            Settings = settings;
            Shells = shells;
            Models = models;
            Plugins = plugins;
            _workRoot = workRoot;
        }

        public DysonUiAgentSessionRuntimeConfigBuilder Builder { get; }
        public IDysonWorkDirectoryRepository WorkDirectories { get; }
        public IDysonWorkDirectoryConfigurationRepository WorkDirectoryConfigurations { get; }
        public IDysonSubjectSettingsRepository Settings { get; }
        public IDysonConfiguredShellRepository Shells { get; }
        public IDysonModelRepository Models { get; }
        public IDysonPluginInstallationRepository Plugins { get; }

        public static Task<Harness> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subject = DysonTempDb.Subject();
            var workDirectories = DysonTempDb.WorkDirectories(accessor, subject);
            var workDirectoryConfigurations = DysonTempDb.WorkDirectoryConfigurations(accessor, subject);
            var settings = DysonTempDb.Settings(accessor, subject);
            var shells = DysonTempDb.Shells(accessor, subject);
            var models = DysonTempDb.Models(accessor, subject);
            var plugins = DysonTempDb.Plugins(accessor, subject);
            var grants = new DysonPluginMcpGrantRepository(accessor, subject);
            var catalog = new DysonPluginCatalogService(plugins);
            var contributions = new DysonPluginContributionResolver();
            var mcpResolver = new DysonPluginMcpResolver();
            var grantService = new DysonPluginMcpGrantService(plugins, grants, catalog, mcpResolver);
            var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-config-builder-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workRoot);
            var builder = new DysonUiAgentSessionRuntimeConfigBuilder(
                workDirectories,
                workDirectoryConfigurations,
                settings,
                shells,
                models,
                catalog,
                contributions,
                grantService,
                mcpResolver);
            return Task.FromResult(new Harness(
                connection,
                builder,
                workDirectories,
                workDirectoryConfigurations,
                settings,
                shells,
                models,
                plugins,
                workRoot));
        }

        public async Task<SeededWork> SeedWorkDirectoryAsync()
        {
            var created = await WorkDirectories.CreateAsync(_workRoot, "ConfigBuilder")
                .ConfigureAwait(false);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            return new SeededWork(created.Value, _workRoot);
        }

        public async Task<SeededProviders> SeedProvidersAsync()
        {
            var demoProvider = await Models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Local",
                ProviderKind = DysonProviderKinds.Demo,
            }).ConfigureAwait(false);
            Assert.True(demoProvider.IsSuccess, demoProvider.IsError ? demoProvider.Error : null);
            var demoSlug = await Models.AddSlugAsync(demoProvider.Value, "demo-config", "Demo Config")
                .ConfigureAwait(false);
            Assert.True(demoSlug.IsSuccess, demoSlug.IsError ? demoSlug.Error : null);

            var openAiProvider = await Models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "OpenAI Local",
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = "https://example.invalid/v1",
                ApiKey = "sk-test",
            }).ConfigureAwait(false);
            Assert.True(openAiProvider.IsSuccess, openAiProvider.IsError ? openAiProvider.Error : null);
            var openAiSlug = await Models.AddSlugAsync(openAiProvider.Value, "gpt-test", "GPT Test")
                .ConfigureAwait(false);
            Assert.True(openAiSlug.IsSuccess, openAiSlug.IsError ? openAiSlug.Error : null);

            return new SeededProviders(demoSlug.Value, openAiSlug.Value);
        }

        public async Task AddPluginAgentAsync(
            PluginPackage plugin,
            Guid workDirectoryId,
            string agentId,
            string relativePath)
        {
            var created = await Plugins.UpsertAsync(new DysonPluginInstallationEntity
            {
                NormalizedPluginId = plugin.Id,
                DisplayName = plugin.Id,
                Version = "1",
                SourceKind = "LocalFolder",
                SourceLocation = plugin.Root,
                PackageFormat = "Cursor",
                InstallScope = DysonPluginStorageValues.ProjectScope,
                WorkDirectoryId = workDirectoryId,
                IsEnabled = true,
                Status = "Installed",
                PackageRoot = plugin.Root,
                ComponentInventoryJson = JsonSerializer.Serialize(new[]
                {
                    new DysonResolvedPluginComponent
                    {
                        Id = agentId,
                        Kind = DysonPluginComponentKind.Agent,
                        RelativePath = relativePath,
                        IsSupported = true,
                        EnabledByDefault = true,
                    },
                }),
                DiagnosticsJson = "[]",
            }).ConfigureAwait(false);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            try
            {
                Directory.Delete(_workRoot, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private sealed record SeededWork(Guid WorkDirectoryId, string WorkRoot);

    private sealed record SeededProviders(Guid DemoSlugId, Guid OpenAiSlugId);

    private sealed class PluginPackage : IDisposable
    {
        public PluginPackage(string id, string? root = null)
        {
            Id = id;
            Root = root ?? Path.Combine(Path.GetTempPath(), $"dyson-config-plugin-{id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Id { get; }
        public string Root { get; }

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }
}

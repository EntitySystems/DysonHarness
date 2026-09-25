using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>
/// Meta plan viewer is DB-backed (<c>metaplan:</c> display path); Build enqueues a
/// session message and does not set Status=Building.
/// </summary>
public class DysonMetaPlanViewerTests
{
    [Fact]
    public async Task OpenMetaPlan_loads_markdown_from_repository_not_disk()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-meta-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await ctx.WorkDirectories.CreateAsync(workRoot, "MetaPlanView");
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            const string markdown = "# Hello meta plan\n\nDo the thing.\n";
            var created = await ctx.Plans.CreateAsync(
                wd.Value,
                DysonPlanKind.MetaPlan,
                "Hello World",
                markdown,
                planRelativePath: null);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            var opened = await ctx.Host.OpenMetaPlanAsync(created.Value, wd.Value);
            Assert.True(opened.IsSuccess, opened.IsError ? opened.Error : null);

            Assert.NotNull(ctx.Host.FileViewer);
            Assert.Equal("metaplan:" + created.Value + "/hello-world.md", ctx.Host.FileViewer.RelativePath);
            Assert.Equal(markdown, ctx.Host.FileViewer.Content);
            Assert.False(ctx.Host.FileViewer.IsLoading);
            Assert.NotEmpty(ctx.Host.FileViewer.MarkdownBlocks);
            Assert.Null(ctx.Host.FileViewer.AbsolutePath);
            Assert.False(ctx.Host.FileViewer.CanOpenInDefaultEditor);
            Assert.True(DysonMetaPlanDisplayPath.TryParse(ctx.Host.FileViewer.RelativePath, out var parsed));
            Assert.Equal(created.Value, parsed);
            Assert.Empty(ctx.Host.FileViewer.Actions);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PromptMetaPlanBuild_enqueues_message_and_leaves_status()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-meta-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await ctx.WorkDirectories.CreateAsync(workRoot, "MetaPlanBuild");
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            var create = await ctx.Models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Local",
                ProviderKind = DysonProviderKinds.Demo,
            });
            Assert.True(create.IsSuccess, create.IsError ? create.Error : null);

            var slug = await ctx.Models.AddSlugAsync(create.Value, "demo-a", "Demo A");
            Assert.True(slug.IsSuccess, slug.IsError ? slug.Error : null);

            var started = await ctx.Host.StartNewSessionAsync(
                DysonAgentModes.MetaAgent, slug.Value, wd.Value);
            Assert.True(started.IsSuccess, started.IsError ? started.Error : null);

            var created = await ctx.Plans.CreateAsync(
                wd.Value,
                DysonPlanKind.MetaPlan,
                "Ship it",
                "# Ship\n",
                planRelativePath: null);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            var session = ctx.Host.Session
                ?? throw new InvalidOperationException("Expected focused session.");
            ctx.Host.MarkSessionBusyForTests(session.PersistenceId);

            var built = await ctx.Host.PromptMetaPlanBuildAsync(created.Value, "Ship it");
            Assert.True(built.IsSuccess, built.IsError ? built.Error : null);

            var expected = DysonMetaPlanDisplayPath.FormatBuildPrompt(created.Value, "Ship it");
            if (ctx.Host.QueuedPrompts.Count > 0)
            {
                Assert.Equal(expected, Assert.Single(ctx.Host.QueuedPrompts).Text);
            }
            else
            {
                var turn = session.InFlightPromptTurn
                    ?? session.Turns.LastOrDefault();
                Assert.NotNull(turn);
                Assert.Contains(
                    expected,
                    turn.FormatInjectedUserCommentsForTranscript(),
                    StringComparison.Ordinal);
            }

            var loaded = await ctx.Plans.GetAsync(created.Value, wd.Value);
            Assert.True(loaded.IsSuccess, loaded.IsError ? loaded.Error : null);
            Assert.Equal(DysonPlanStatus.Draft, loaded.Value.Status);
            Assert.Null(loaded.Value.BuildAgentId);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task OpenMetaPlan_adds_build_action_when_meta_runtime_is_live()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-meta-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await ctx.WorkDirectories.CreateAsync(workRoot, "MetaPlanAction");
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            var create = await ctx.Models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Local",
                ProviderKind = DysonProviderKinds.Demo,
            });
            Assert.True(create.IsSuccess, create.IsError ? create.Error : null);

            var slug = await ctx.Models.AddSlugAsync(create.Value, "demo-a", "Demo A");
            Assert.True(slug.IsSuccess, slug.IsError ? slug.Error : null);

            var started = await ctx.Host.StartNewSessionAsync(
                DysonAgentModes.MetaAgent, slug.Value, wd.Value);
            Assert.True(started.IsSuccess, started.IsError ? started.Error : null);

            var created = await ctx.Plans.CreateAsync(
                wd.Value,
                DysonPlanKind.MetaPlan,
                "Ready",
                "# Ready\n",
                planRelativePath: null);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            var opened = await ctx.Host.OpenMetaPlanAsync(created.Value, wd.Value);
            Assert.True(opened.IsSuccess, opened.IsError ? opened.Error : null);
            var action = Assert.Single(ctx.Host.FileViewer!.Actions);
            Assert.Equal("Build plan", action.Label);
            Assert.True(action.IsPrimary);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class HostContext : IAsyncDisposable
    {
        private readonly SqliteConnection _conn;

        private HostContext(
            SqliteConnection conn,
            DysonUiHost host,
            IDysonPlanRepository plans,
            IDysonModelRepository models,
            IDysonWorkDirectoryRepository workDirectories)
        {
            _conn = conn;
            Host = host;
            Plans = plans;
            Models = models;
            WorkDirectories = workDirectories;
        }

        public DysonUiHost Host { get; }
        public IDysonPlanRepository Plans { get; }
        public IDysonModelRepository Models { get; }
        public IDysonWorkDirectoryRepository WorkDirectories { get; }

        public static Task<HostContext> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
            var models = DysonTempDb.Models(accessor);
            var sessions = DysonTempDb.Sessions(accessor);
            var workDirs = DysonTempDb.WorkDirectories(accessor);
            var workDirConfigs = DysonTempDb.WorkDirectoryConfigurations(accessor);
            var plans = DysonTempDb.Plans(accessor);
            var settings = DysonTempDb.Settings(accessor);
            var shells = DysonTempDb.Shells(accessor);
            var plugins = DysonTempDb.Plugins(accessor);
            var grants = new DysonPluginMcpGrantRepository(accessor, DysonFixedLocalSubjectContext.Instance);
            var catalog = new DysonPluginCatalogService(plugins);
            var lifecycle = new DysonPluginLifecycleService(plugins);
            var contributions = new DysonPluginContributionResolver();
            var mcpResolver = new DysonPluginMcpResolver();
            var grantService = new DysonPluginMcpGrantService(plugins, grants, catalog, mcpResolver);
            var host = new DysonUiHost(
                sessions,
                models,
                workDirs,
                workDirConfigs,
                plans,
                settings,
                shells,
                new HttpClient(),
                new DysonCliProxyHost(new HttpClient()),
                new DysonFilePreviewStore(),
                catalog,
                contributions,
                grantService,
                mcpResolver,
                lifecycle,
                new ThemeService(new ThemeJsRuntime("light", "#ABC")));
            return Task.FromResult(new HostContext(conn, host, plans, models, workDirs));
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync().ConfigureAwait(false);
            await _conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ThemeJsRuntime(string theme, string accent) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? value = identifier switch
            {
                "dysonTheme.get" => null,
                "dysonTheme.getResolved" => new { theme, accentHex = accent },
                "dysonTheme.apply" => null,
                _ => throw new InvalidOperationException($"Unexpected JS call: {identifier}"),
            };

            if (value is null)
                return ValueTask.FromResult(default(TValue)!);

            var json = JsonSerializer.Serialize(value);
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
    }
}

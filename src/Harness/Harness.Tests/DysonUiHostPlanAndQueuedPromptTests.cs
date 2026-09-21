using System.Reflection;
using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>
/// Host wiring: OpenAI sessions receive the live plan repository, and queued prompts keep full Text.
/// </summary>
public class DysonUiHostPlanAndQueuedPromptTests
{
    [Fact]
    public async Task OpenAi_session_from_host_sees_live_plan_repository()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-host-plans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await ctx.WorkDirectories.CreateAsync(workRoot, "PlansHost");
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            var create = await ctx.Models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "OpenAI Local",
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = "https://example.invalid/v1",
                ApiKey = "sk-test",
            });
            Assert.True(create.IsSuccess, create.IsError ? create.Error : null);

            var slug = await ctx.Models.AddSlugAsync(create.Value, "gpt-test", "GPT Test");
            Assert.True(slug.IsSuccess, slug.IsError ? slug.Error : null);

            var started = await ctx.Host.StartNewSessionAsync(
                DysonAgentModes.MetaAgent, slug.Value, wd.Value);
            Assert.True(started.IsSuccess, started.IsError ? started.Error : null);

            var session = Assert.IsType<OpenAiCompatibleAgentSession>(ctx.Host.Session);
            var field = typeof(OpenAiCompatibleAgentSession).GetField(
                "_plans", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var wired = field.GetValue(session) as IDysonPlanRepository;
            Assert.Same(ctx.Plans, wired);

            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(
                session,
                workRoot,
                http,
                store: ctx.Sessions,
                workDirectoryId: wd.Value,
                plans: wired);
            var listed = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "1",
                ToolName = "ListPlans",
                Stage = 0,
                ArgumentsJson = "{}",
            });
            Assert.False(listed.IsError, listed.Content);
            Assert.DoesNotContain(
                "Plan repository is not available.",
                listed.Content,
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Queued_multiline_prompt_round_trips_full_Text()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-host-queue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await ctx.WorkDirectories.CreateAsync(workRoot, "QueueHost");
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
                DysonAgentModes.Work, slug.Value, wd.Value);
            Assert.True(started.IsSuccess, started.IsError ? started.Error : null);

            var session = ctx.Host.Session
                ?? throw new InvalidOperationException("Expected focused session.");
            ctx.Host.MarkSessionBusyForTests(session.PersistenceId);

            const string prompt = "line one\nline two\nline three";
            var queued = await ctx.Host.PromptAsync(prompt);
            Assert.True(queued.IsSuccess, queued.IsError ? queued.Error : null);

            var item = Assert.Single(ctx.Host.QueuedPrompts);
            Assert.Equal("line one", item.FirstLine);
            Assert.Equal(prompt, item.Text);
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
            IDysonWorkDirectoryRepository workDirectories,
            IDysonSessionRepository sessions)
        {
            _conn = conn;
            Host = host;
            Plans = plans;
            Models = models;
            WorkDirectories = workDirectories;
            Sessions = sessions;
        }

        public DysonUiHost Host { get; }
        public IDysonPlanRepository Plans { get; }
        public IDysonModelRepository Models { get; }
        public IDysonWorkDirectoryRepository WorkDirectories { get; }
        public IDysonSessionRepository Sessions { get; }

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
            return Task.FromResult(new HostContext(conn, host, plans, models, workDirs, sessions));
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

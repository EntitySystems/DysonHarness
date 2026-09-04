using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>
/// ponytail: host Worktree checkbox persist — workdir forkWorktree + session meta, no git-init.
/// </summary>
public sealed class DysonUiHostWorktreeComposerTests
{
    [Fact]
    public async Task SetWorktreeEnabled_persists_workdir_forkWorktree_and_session_meta()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;

        var models = DysonTempDb.Models(accessor);
        var sessions = DysonTempDb.Sessions(accessor);
        var workDirs = DysonTempDb.WorkDirectories(accessor);
        var workDirConfigs = DysonTempDb.WorkDirectoryConfigurations(accessor);
        var settings = DysonTempDb.Settings(accessor);
        var shells = DysonTempDb.Shells(accessor);
        var plugins = DysonTempDb.Plugins(accessor);
        var grants = new DysonPluginMcpGrantRepository(accessor, DysonFixedLocalSubjectContext.Instance);

        var catalog = new DysonPluginCatalogService(plugins);
        var lifecycle = new DysonPluginLifecycleService(plugins);
        var contributions = new DysonPluginContributionResolver();
        var mcpResolver = new DysonPluginMcpResolver();
        var grantService = new DysonPluginMcpGrantService(plugins, grants, catalog, mcpResolver);

        using var http = new HttpClient();
        var cliProxy = new DysonCliProxyHost(http);
        await using var host = new DysonUiHost(
            sessions,
            models,
            workDirs,
            workDirConfigs,
            settings,
            shells,
            http,
            cliProxy,
            new DysonFilePreviewStore(),
            catalog,
            contributions,
            grantService,
            mcpResolver,
            lifecycle,
            new ThemeService(new ThemeJsRuntime("light", "#ABC")));

        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-wt-composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await workDirs.CreateAsync(workRoot, "WorktreeComposer");
            if (wd.IsError)
                throw new InvalidOperationException(wd.Error);

            host.SetComposerWorkDirectoryId(wd.Value);
            var preSession = await host.SetWorktreeEnabledAsync(true);
            if (preSession.IsError)
                throw new InvalidOperationException(preSession.Error);

            var cfg = await workDirConfigs.GetAsync(wd.Value);
            if (cfg.IsError)
                throw new InvalidOperationException(cfg.Error);
            if (!DysonWorkDirectoryConfig.TryGetForkWorktree(cfg.Value))
                throw new InvalidOperationException("Pre-session toggle must persist forkWorktree=true.");

            var create = await models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Local",
                ProviderKind = DysonProviderKinds.Demo,
            });
            if (create.IsError)
                throw new InvalidOperationException(create.Error);

            var slug = await models.AddSlugAsync(create.Value, "demo-a", "Demo A");
            if (slug.IsError)
                throw new InvalidOperationException(slug.Error);

            var started = await host.StartNewSessionAsync(
                DysonAgentModes.Work,
                slug.Value,
                wd.Value);
            if (started.IsError)
                throw new InvalidOperationException(started.Error);

            var session = host.Session
                ?? throw new InvalidOperationException("Expected focused session.");

            var enabled = await host.SetWorktreeEnabledAsync(true);
            if (enabled.IsError)
                throw new InvalidOperationException(enabled.Error);
            if (!session.WorktreeEnabled)
                throw new InvalidOperationException("Live session.WorktreeEnabled must be true.");
            if (!host.WorktreeChecked || host.WorktreeLocked)
                throw new InvalidOperationException("Checkbox should be checked and unlocked before checkout.");
            if (!session.SystemPrompt.Contains(
                    DysonAgentSystemPrompts.WorktreeEnabledNotCreatedPromptBlock,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Enabled session must include pending worktree prompt block.");
            }

            var full = await sessions.GetFullSessionAsync(session.PersistenceId);
            if (full.IsError)
                throw new InvalidOperationException(full.Error);
            if (!full.Value.Session.WorktreeEnabled)
                throw new InvalidOperationException("Session meta must persist WorktreeEnabled=true.");

            session.WorktreeAbsolutePath = Path.Combine(workRoot, "fake-wt");
            session.WorktreeBranch = "dyson/abcd1234";
            if (!host.WorktreeLocked || !host.WorktreeChecked)
                throw new InvalidOperationException("Non-empty path must lock and keep the checkbox checked.");

            var uncheck = await host.SetWorktreeEnabledAsync(false);
            if (!uncheck.IsError)
                throw new InvalidOperationException("Locked uncheck must fail.");
            if (!session.WorktreeEnabled)
                throw new InvalidOperationException("Locked uncheck must leave WorktreeEnabled=true.");
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best effort */ }
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

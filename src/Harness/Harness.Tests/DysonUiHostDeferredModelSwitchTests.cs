using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>
/// ponytail: busy SetSessionModelSlugAsync stashes without mutating Provider; flush applies before next prompt.
/// </summary>
public class DysonUiHostDeferredModelSwitchTests
{
    [Fact]
    public async Task Busy_switch_stashes_then_flush_applies_provider()
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

        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-defer-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await workDirs.CreateAsync(workRoot, "DeferModel");
            if (wd.IsError)
                throw new InvalidOperationException(wd.Error);

            var create = await models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Local",
                ProviderKind = DysonProviderKinds.Demo,
            });
            if (create.IsError)
                throw new InvalidOperationException(create.Error);

            var slugA = await models.AddSlugAsync(create.Value, "demo-a", "Demo A");
            if (slugA.IsError)
                throw new InvalidOperationException(slugA.Error);

            var slugB = await models.AddSlugAsync(create.Value, "demo-b", "Demo B");
            if (slugB.IsError)
                throw new InvalidOperationException(slugB.Error);

            var started = await host.StartNewSessionAsync(
                DysonAgentModes.Work,
                slugA.Value,
                wd.Value);
            if (started.IsError)
                throw new InvalidOperationException(started.Error);

            var session = host.Session
                ?? throw new InvalidOperationException("Expected focused session after StartNewSession.");
            var sessionId = session.PersistenceId;
            if (sessionId == Guid.Empty)
                throw new InvalidOperationException("Expected persisted session.");
            Assert.Equal("light", session.Config.UiTheme.Theme);
            Assert.Equal("#aabbcc", session.Config.UiTheme.AccentHex);

            if (session.Provider is not DemoDysonAgentProvider before
                || before.SlugId != slugA.Value)
            {
                throw new InvalidOperationException("Session should start on demo-a.");
            }

            host.MarkSessionBusyForTests(sessionId);
            if (!host.IsBusy)
                throw new InvalidOperationException("Expected IsBusy after MarkSessionBusyForTests.");

            var switched = await host.SetSessionModelSlugAsync(slugB.Value);
            if (switched.IsError)
                throw new InvalidOperationException($"Busy switch should succeed: {switched.Error}");
            if (host.LastError is not null)
                throw new InvalidOperationException($"Busy switch should clear LastError, got: {host.LastError}");

            if (session.Provider is not DemoDysonAgentProvider mid
                || mid.SlugId != slugA.Value)
            {
                throw new InvalidOperationException("Busy switch must not mutate Provider mid-turn.");
            }

            host.ClearSessionBusyForTests(sessionId);
            await host.FlushPendingSessionModelSlugForTestsAsync(sessionId);

            if (session.Provider is not DemoDysonAgentProvider after
                || after.SlugId != slugB.Value)
            {
                throw new InvalidOperationException("Flush must apply pending slug before next prompt.");
            }

            var plan = await host.SetSessionAgentModeAsync(DysonAgentModes.Plan);
            if (plan.IsError)
                throw new InvalidOperationException(plan.Error);
            if (!string.Equals(session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Expected Plan after SetSessionAgentModeAsync.");

            var idleSwitch = await host.SetSessionModelSlugAsync(slugA.Value);
            if (idleSwitch.IsError)
                throw new InvalidOperationException(idleSwitch.Error);
            if (!string.Equals(session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Switching model must not change agent mode.");
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

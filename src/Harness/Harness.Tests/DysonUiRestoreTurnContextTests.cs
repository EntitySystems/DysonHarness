using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>
/// ponytail: UI restore must clear ContextSummary the same way MCP restore does,
/// or later transcripts keep FormatSummaryStub instead of the turn body.
/// </summary>
public class DysonUiRestoreTurnContextTests
{
    [Fact]
    public async Task Restore_clears_summary_only_when_the_turn_was_excluded()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;

        var models = DysonTempDb.Models(accessor);
        var sessions = DysonTempDb.Sessions(accessor);
        var workDirs = DysonTempDb.WorkDirectories(accessor);
        var plugins = DysonTempDb.Plugins(accessor);
        var grants = new DysonPluginMcpGrantRepository(accessor, DysonFixedLocalSubjectContext.Instance);
        var catalog = new DysonPluginCatalogService(plugins);
        var lifecycle = new DysonPluginLifecycleService(plugins);
        var contributions = new DysonPluginContributionResolver();
        var mcpResolver = new DysonPluginMcpResolver();
        var grantService = new DysonPluginMcpGrantService(plugins, grants, catalog, mcpResolver);

        using var http = new HttpClient();
        await using var host = new DysonUiHost(
            sessions,
            models,
            workDirs,
            DysonTempDb.WorkDirectoryConfigurations(accessor),
            DysonTempDb.Plans(accessor),
            DysonTempDb.Settings(accessor),
            DysonTempDb.Shells(accessor),
            http,
            new DysonCliProxyHost(http),
            new DysonFilePreviewStore(),
            catalog,
            contributions,
            grantService,
            mcpResolver,
            lifecycle,
            new ThemeService(new ThemeJsRuntime("dark", "#e09b58")));

        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-ui-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var wd = await workDirs.CreateAsync(workRoot, "UiRestore");
            if (wd.IsError)
                throw new InvalidOperationException(wd.Error);

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

            var started = await host.StartNewSessionAsync(DysonAgentModes.Work, slug.Value, wd.Value);
            if (started.IsError)
                throw new InvalidOperationException(started.Error);

            var session = host.Session
                ?? throw new InvalidOperationException("Expected a focused session.");

            const string body = "ORIGINAL_BODY_DROP_RESTORE";
            var prompted = await host.PromptAsync(body);
            if (prompted.IsError)
                throw new InvalidOperationException(prompted.Error);

            var turn = session.Turns.FirstOrDefault(t =>
                t.Instruction is not null && t.Instruction.Contains(body, StringComparison.Ordinal));
            if (turn is null)
                throw new InvalidOperationException("Prompt must append a turn with the original instruction.");

            turn.ContextSummary = "KEEP_SUMMARY";
            var kept = await host.RestoreTurnContextAsync(session.PersistenceId, turn.Id);
            if (kept.IsError)
                throw new InvalidOperationException(kept.Error);
            if (turn.IsExcludedFromContext || turn.ContextSummary != "KEEP_SUMMARY")
                throw new InvalidOperationException("Restore of a turn that was not dropped must keep its summary.");

            turn.IsExcludedFromContext = true;
            turn.ContextSummary = "STUB_SHOULD_VANISH";
            var restored = await host.RestoreTurnContextAsync(session.PersistenceId, turn.Id);
            if (restored.IsError)
                throw new InvalidOperationException(restored.Error);
            if (turn.IsExcludedFromContext || turn.ContextSummary is not null)
                throw new InvalidOperationException("UI restore must clear the exclusion and the summary.");

            var history = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null).Messages.ToJsonString();
            if (!history.Contains(body, StringComparison.Ordinal)
                || history.Contains("STUB_SHOULD_VANISH", StringComparison.Ordinal)
                || history.Contains("[contextSummary]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Restored history must contain the original body, not the summary stub: " + history);
            }
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

            var json = System.Text.Json.JsonSerializer.Serialize(value);
            return ValueTask.FromResult(System.Text.Json.JsonSerializer.Deserialize<TValue>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
    }
}

using System.Text;
using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>In-memory file viewer: bus request, async prepare, text-preview iframe, epoch.</summary>
public class DysonFileViewerContentTests
{
    [Fact]
    public async Task RequestOpen_is_non_blocking_then_ready_via_changed_events()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var changed = new List<DysonFileViewerChangedEvent>();
        using var sub = host.Bus.Subscribe<DysonFileViewerChangedEvent>(
            host.BusScopeKey,
            e => changed.Add(e)).Value;

        var huge = new string('a', 70 * 1024);
        host.RequestOpenFileViewerContent("tool-calls/InspectSubagentLog.json", huge);

        Assert.True(
            host.FileViewer is null
            || host.FileViewer.IsLoading
            || host.FileViewer.Content.Length < huge.Length,
            "RequestOpen must return before the huge body is on overlay state.");

        await host.OpenFileViewerContentAsync("tool-calls/InspectSubagentLog.json", huge);

        Assert.Contains(changed, e => e.Viewer is { IsLoading: true, Content: "" });
        Assert.NotNull(host.FileViewer);
        Assert.False(host.FileViewer.IsLoading);
        Assert.True(host.FileViewer.IsTextPreview);
        Assert.Equal("", host.FileViewer.Content);
        Assert.False(string.IsNullOrEmpty(host.FileViewer.TextPreviewUrl));
        Assert.Equal(changed[^1].Viewer, host.FileViewer);
    }

    [Fact]
    public async Task Small_markdown_stays_on_circuit_with_blocks()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var actions = new[]
        {
            new DysonFileViewerAction
            {
                Label = "Download skill",
                Invoke = static () => Task.CompletedTask,
                IsPrimary = true,
            },
        };

        await host.OpenFileViewerContentAsync("skillsdirectory:demo/SKILL.md", "# Skill", actions);

        Assert.NotNull(host.FileViewer);
        Assert.Equal("# Skill", host.FileViewer.Content);
        Assert.False(host.FileViewer.IsLoading);
        Assert.False(host.FileViewer.IsTextPreview);
        Assert.NotEmpty(host.FileViewer.MarkdownBlocks);
        Assert.Null(host.FileViewer.Error);
        Assert.Single(host.FileViewer.Actions);
        Assert.Equal("Download skill", host.FileViewer.Actions[0].Label);
    }

    [Fact]
    public async Task Json_in_memory_uses_text_preview_and_close_revokes()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var store = ctx.Previews;
        var compact = "{\"subagentId\":1,\"lines\":[\"a\"]}";
        var changed = new List<DysonFileViewerChangedEvent>();
        using var sub = host.Bus.Subscribe<DysonFileViewerChangedEvent>(
            host.BusScopeKey,
            e => changed.Add(e)).Value;

        await host.OpenFileViewerContentAsync("tool-calls/InspectSubagentLog.json", compact);

        Assert.NotNull(host.FileViewer);
        Assert.True(host.FileViewer.IsTextPreview);
        Assert.Equal("", host.FileViewer.Content);
        Assert.False(string.IsNullOrEmpty(host.FileViewer.TextPreviewUrl));
        var previewId = host.FileViewer.TextPreviewId;
        Assert.False(string.IsNullOrEmpty(previewId));
        Assert.True(store.TryGet(previewId!, out var entry));
        Assert.Equal("text/plain; charset=utf-8", entry.ContentType);
        var text = Encoding.UTF8.GetString(entry.Bytes);
        using var parsed = JsonDocument.Parse(text);
        Assert.True(text.Contains('\n') || text.Contains("  "), "JSON should be pretty-printed.");

        host.CloseFileViewer();
        Assert.Null(host.FileViewer);
        Assert.False(store.TryGet(previewId!, out _));
        Assert.Contains(changed, e => e.Viewer is null);
    }

    [Fact]
    public async Task Large_markdown_uses_text_preview_not_circuit_body()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var hugeMd = "# " + new string('x', 70 * 1024);

        await host.OpenFileViewerContentAsync("notes.md", hugeMd);

        Assert.NotNull(host.FileViewer);
        Assert.True(host.FileViewer.IsTextPreview);
        Assert.Equal("", host.FileViewer.Content);
        Assert.Empty(host.FileViewer.MarkdownBlocks);
        Assert.True(ctx.Previews.TryGet(host.FileViewer.TextPreviewId!, out var entry));
        Assert.Equal(hugeMd, Encoding.UTF8.GetString(entry.Bytes));
    }

    [Fact]
    public async Task Stale_open_keeps_later_file_and_drops_earlier_preview()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var store = ctx.Previews;

        var first = host.OpenFileViewerContentAsync("a.json", """{"a":1}""");
        var second = host.OpenFileViewerContentAsync("b.json", """{"b":2}""");
        await Task.WhenAll(first, second);

        Assert.NotNull(host.FileViewer);
        Assert.Equal("b.json", host.FileViewer.RelativePath);
        var keptId = host.FileViewer.TextPreviewId;
        Assert.False(string.IsNullOrEmpty(keptId));
        Assert.True(store.TryGet(keptId!, out var kept));
        Assert.Contains("\"b\"", Encoding.UTF8.GetString(kept.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_during_prepare_stays_closed()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var huge = "{" + new string('x', 80 * 1024) + "}";

        var open = host.OpenFileViewerContentAsync("tool-calls/big.json", huge);
        host.CloseFileViewer();
        await open;

        Assert.Null(host.FileViewer);
    }

    [Fact]
    public async Task In_memory_open_does_not_publish_overlay_kind()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var overlay = 0;
        var viewerChanged = 0;
        using var hostSub = host.Bus.Subscribe<DysonHostStateChangedEvent>(
            host.BusScopeKey,
            e =>
            {
                if ((e.Kind & DysonHostChangeKind.Overlay) != 0)
                    Interlocked.Increment(ref overlay);
            }).Value;
        using var viewerSub = host.Bus.Subscribe<DysonFileViewerChangedEvent>(
            host.BusScopeKey,
            _ => Interlocked.Increment(ref viewerChanged)).Value;

        await host.OpenFileViewerContentAsync("skillsdirectory:demo/SKILL.md", "# Skill");
        await Task.Delay(120);

        Assert.Equal(0, overlay);
        Assert.True(viewerChanged >= 2);
        Assert.NotNull(host.FileViewer);
        Assert.Equal("# Skill", host.FileViewer.Content);
    }

    private sealed class HostContext : IAsyncDisposable
    {
        private readonly SqliteConnection _conn;

        private HostContext(SqliteConnection conn, DysonUiHost host, DysonFilePreviewStore previews)
        {
            _conn = conn;
            Host = host;
            Previews = previews;
        }

        public DysonUiHost Host { get; }
        public DysonFilePreviewStore Previews { get; }

        public static Task<HostContext> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
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
            var previews = new DysonFilePreviewStore();
            var host = new DysonUiHost(
                sessions,
                models,
                workDirs,
                workDirConfigs,
                settings,
                shells,
                new HttpClient(),
                new DysonCliProxyHost(new HttpClient()),
                previews,
                catalog,
                contributions,
                grantService,
                mcpResolver,
                lifecycle,
                new ThemeService(new ThemeJsRuntime("light", "#ABC")));
            return Task.FromResult(new HostContext(conn, host, previews));
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

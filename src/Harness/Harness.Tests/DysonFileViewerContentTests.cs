using System.Text;
using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Markdown;
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
    public async Task Long_markdown_renders_blocks_not_text_preview()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var longMd = LongPlan(100_000);

        await host.OpenFileViewerContentAsync("notes.md", longMd);

        Assert.NotNull(host.FileViewer);
        Assert.Null(host.FileViewer.Error); Assert.False(host.FileViewer.IsLoading); Assert.True(host.FileViewer.IsMarkdown);
        Assert.False(host.FileViewer.IsTextPreview);
        Assert.Null(host.FileViewer.TextPreviewId);
        Assert.Equal(longMd, host.FileViewer.Content);
        Assert.True(host.FileViewer.MarkdownBlocks.Count > 100);
        Assert.Contains(host.FileViewer.MarkdownBlocks, b => b.Html.ToString().Contains("<table>", StringComparison.Ordinal));
        Assert.Contains(host.FileViewer.MarkdownBlocks, b => b.Html.ToString().Contains("<h2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Markdown_over_render_ceiling_uses_text_preview()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var hugeMd = "# " + new string('x', DysonFileViewerMarkdown.MaxRenderChars + 1);

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

    [Fact]
    public void Audio_extensions_map_to_audio_mime_types_case_insensitively()
    {
        Assert.Equal("audio/mpeg", DysonAudioFiles.ContentTypeFor(".dyson/composer-uploads/cannon_shot_2.MP3"));
        Assert.Equal("audio/wav", DysonAudioFiles.ContentTypeFor("a.wav"));
        Assert.Equal("audio/ogg", DysonAudioFiles.ContentTypeFor("a.Ogg"));
        Assert.Equal("audio/ogg", DysonAudioFiles.ContentTypeFor("a.oga"));
        Assert.Equal("audio/ogg", DysonAudioFiles.ContentTypeFor("a.opus"));
        Assert.Equal("audio/flac", DysonAudioFiles.ContentTypeFor("a.flac"));
        Assert.Equal("audio/mp4", DysonAudioFiles.ContentTypeFor("a.m4a"));
        Assert.Equal("audio/aac", DysonAudioFiles.ContentTypeFor("a.aac"));
        Assert.Equal("audio/webm", DysonAudioFiles.ContentTypeFor("a.weba"));

        Assert.True(DysonAudioFiles.IsAudioPath("song.Mp3"));
        Assert.False(DysonAudioFiles.IsAudioPath("notes.txt"));
        Assert.False(DysonAudioFiles.IsAudioPath("clip.mp4"));
        Assert.False(DysonAudioFiles.IsAudioPath("mp3"));
        Assert.False(DysonAudioFiles.IsAudioPath(""));
        Assert.False(DysonAudioFiles.IsAudioPath(null));
    }

    [Fact]
    public async Task Audio_file_opens_player_streaming_from_disk_and_close_revokes()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-fv-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workRoot, "clips"));
        try
        {
            var onDisk = Path.Combine(workRoot, "clips", "shot.MP3");
            await File.WriteAllBytesAsync(onDisk, [0xFF, 0xFB, 0x90, 0x00]);

            await host.OpenFileViewerAsync("clips/shot.MP3", workRoot);

            Assert.NotNull(host.FileViewer);
            Assert.True(host.FileViewer.IsAudio);
            Assert.Null(host.FileViewer.Error);
            Assert.Equal("", host.FileViewer.Content);
            Assert.True(host.FileViewer.CanOpenInDefaultEditor);
            var previewId = host.FileViewer.AudioPreviewId;
            Assert.False(string.IsNullOrEmpty(previewId));
            Assert.Equal(DysonFilePreviewStore.UrlFor(previewId!), host.FileViewer.AudioPreviewUrl);
            Assert.True(ctx.Previews.TryGet(previewId!, out var entry));
            Assert.Equal("audio/mpeg", entry.ContentType);
            Assert.Equal(Path.GetFullPath(onDisk), entry.FilePath);
            Assert.Empty(entry.Bytes);

            host.CloseFileViewer();
            Assert.Null(host.FileViewer);
            Assert.False(ctx.Previews.TryGet(previewId!, out _));
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Audio_path_outside_work_directory_is_rejected_without_a_preview()
    {
        await using var ctx = await HostContext.CreateAsync();
        var host = ctx.Host;
        var parent = Path.Combine(Path.GetTempPath(), $"dyson-fv-audio-out-{Guid.NewGuid():N}");
        var workRoot = Path.Combine(parent, "work");
        Directory.CreateDirectory(workRoot);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(parent, "secret.mp3"), [1, 2, 3]);

            await host.OpenFileViewerAsync("../secret.mp3", workRoot);
            Assert.NotNull(host.FileViewer);
            Assert.True(host.FileViewer.IsAudio);
            Assert.False(string.IsNullOrEmpty(host.FileViewer.Error));
            Assert.Null(host.FileViewer.AudioPreviewId);
            Assert.Null(host.FileViewer.AudioPreviewUrl);
            Assert.False(host.FileViewer.CanOpenInDefaultEditor);

            await host.OpenFileViewerAsync("missing.mp3", workRoot);
            Assert.NotNull(host.FileViewer);
            Assert.False(string.IsNullOrEmpty(host.FileViewer.Error));
            Assert.Null(host.FileViewer.AudioPreviewId);

            Assert.Throws<ArgumentException>(() => ctx.Previews.PutFile("relative/a.mp3", "audio/mpeg"));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Workspace_markdown_over_render_ceiling_opens_as_code()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-fv-md-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workRoot, "huge.md"),
                "# " + new string('x', DysonFileViewerMarkdown.MaxRenderChars + 1));

            await ctx.Host.OpenFileViewerAsync("huge.md", workRoot);

            Assert.NotNull(ctx.Host.FileViewer);
            Assert.Null(ctx.Host.FileViewer.Error);
            Assert.False(ctx.Host.FileViewer.IsMarkdown);
            Assert.Empty(ctx.Host.FileViewer.MarkdownBlocks);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task<string> NewWorkRootAsync(params (string Name, byte[] Bytes)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dyson-fv-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (name, bytes) in files)
            await File.WriteAllBytesAsync(Path.Combine(root, name), bytes);
        return root;
    }

    private static void DeleteQuietly(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Swaps the host launcher seams for recorders so no process is ever spawned.</summary>
    private static (List<string> Opened, List<string> Revealed) RecordLaunchers(
        DysonUiHost host,
        VoidResult<string>? openResult = null,
        VoidResult<string>? revealResult = null)
    {
        var opened = new List<string>();
        var revealed = new List<string>();
        host.OpenWithOsDefault = path =>
        {
            opened.Add(path);
            return Task.FromResult(openResult ?? VoidResult<string>.Success);
        };
        host.RevealInFileManager = path =>
        {
            revealed.Add(path);
            return Task.FromResult(revealResult ?? VoidResult<string>.Success);
        };
        return (opened, revealed);
    }

    [Fact]
    public async Task Unsupported_binary_opens_with_os_default_and_is_never_read_as_text()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = await NewWorkRootAsync(("art.psd", [(byte)'8', (byte)'B', (byte)'P', (byte)'S', 0, 1, 0, 0]));
        try
        {
            var (opened, revealed) = RecordLaunchers(ctx.Host);

            await ctx.Host.OpenFileViewerAsync("art.psd", workRoot);

            Assert.Equal(Path.GetFullPath(Path.Combine(workRoot, "art.psd")), Assert.Single(opened));
            Assert.Empty(revealed);
            Assert.Null(ctx.Host.FileViewer);

            // A failing launcher surfaces the error in the viewer and never shows the bytes as text.
            RecordLaunchers(ctx.Host, openResult: VoidResult<string>.AsError("no handler"));
            await ctx.Host.OpenFileViewerAsync("art.psd", workRoot);
            Assert.NotNull(ctx.Host.FileViewer);
            Assert.Equal("no handler", ctx.Host.FileViewer.Error);
            Assert.Equal("", ctx.Host.FileViewer.Content);
        }
        finally
        {
            DeleteQuietly(workRoot);
        }
    }

    [Fact]
    public async Task Executables_are_revealed_never_launched()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = await NewWorkRootAsync(
            ("tool.exe", [(byte)'M', (byte)'Z', 0, 0, 3, 0]),
            ("payload", [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]),
            ("x.dat", [(byte)'M', (byte)'Z', 0x90, 0]));
        try
        {
            var (opened, revealed) = RecordLaunchers(ctx.Host);

            foreach (var name in new[] { "tool.exe", "payload", "x.dat" })
            {
                var before = revealed.Count;
                await ctx.Host.OpenFileViewerAsync(name, workRoot);
                Assert.Equal(before + 1, revealed.Count);
                Assert.Equal(Path.GetFullPath(Path.Combine(workRoot, name)), revealed[^1]);
                Assert.Null(ctx.Host.FileViewer);
            }

            Assert.Empty(opened);

            // Reveal failure: explained in the viewer; the launcher is still never called.
            RecordLaunchers(ctx.Host, revealResult: VoidResult<string>.AsError("no file manager"));
            await ctx.Host.OpenFileViewerAsync("tool.exe", workRoot);
            Assert.NotNull(ctx.Host.FileViewer);
            Assert.Contains("does not open or run it", ctx.Host.FileViewer.Error, StringComparison.Ordinal);
            Assert.Contains("no file manager", ctx.Host.FileViewer.Error, StringComparison.Ordinal);
            Assert.Equal("", ctx.Host.FileViewer.Content);
        }
        finally
        {
            DeleteQuietly(workRoot);
        }
    }

    [Fact]
    public async Task Open_externally_reclassifies_so_a_renamed_executable_is_revealed_not_launched()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = await NewWorkRootAsync(
            ("clip.mp4", [(byte)'M', (byte)'Z', 0x90, 0]),
            ("pic.png", [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]),
            ("movie.mp4", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p']));
        try
        {
            var (opened, revealed) = RecordLaunchers(ctx.Host);

            // Media/image branches run before the sniff gate and offer the external button; the click must not launch.
            foreach (var name in new[] { "clip.mp4", "pic.png" })
            {
                await ctx.Host.OpenFileViewerAsync(name, workRoot);
                Assert.True(ctx.Host.FileViewer!.CanOpenInDefaultEditor);
                Assert.True((await ctx.Host.OpenViewerFileExternallyAsync()).IsSuccess);
                Assert.Equal(Path.GetFullPath(Path.Combine(workRoot, name)), revealed[^1]);
            }

            Assert.Empty(opened);

            await ctx.Host.OpenFileViewerAsync("movie.mp4", workRoot);
            Assert.True((await ctx.Host.OpenViewerFileExternallyAsync()).IsSuccess);
            Assert.Equal(Path.GetFullPath(Path.Combine(workRoot, "movie.mp4")), Assert.Single(opened));
            Assert.Equal(2, revealed.Count);
        }
        finally
        {
            DeleteQuietly(workRoot);
        }
    }

    [Fact]
    public async Task Text_scripts_open_read_only()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = await NewWorkRootAsync(
            ("run.sh", "#!/bin/sh\necho hi\n"u8.ToArray()),
            ("page.html", "<p>hi</p>"u8.ToArray()),
            ("notes.txt", "hello"u8.ToArray()));
        try
        {
            var (opened, revealed) = RecordLaunchers(ctx.Host);

            await ctx.Host.OpenFileViewerAsync("run.sh", workRoot);
            Assert.Equal("#!/bin/sh\necho hi\n", ctx.Host.FileViewer!.Content);
            Assert.False(ctx.Host.FileViewer.CanOpenInDefaultEditor);

            await ctx.Host.OpenFileViewerAsync("page.html", workRoot);
            Assert.Equal("<p>hi</p>", ctx.Host.FileViewer!.Content);
            Assert.False(ctx.Host.FileViewer.CanOpenInDefaultEditor);

            await ctx.Host.OpenFileViewerAsync("notes.txt", workRoot);
            Assert.Equal("hello", ctx.Host.FileViewer!.Content);
            Assert.True(ctx.Host.FileViewer.CanOpenInDefaultEditor);

            Assert.Empty(opened);
            Assert.Empty(revealed);
        }
        finally
        {
            DeleteQuietly(workRoot);
        }
    }

    [Fact]
    public void Video_extensions_map_to_video_mime_types()
    {
        Assert.Equal("video/mp4", DysonVideoFiles.ContentTypeFor("a.mp4"));
        Assert.Equal("video/mp4", DysonVideoFiles.ContentTypeFor("dir/a.M4V"));
        Assert.Equal("video/webm", DysonVideoFiles.ContentTypeFor("a.webm"));
        Assert.Equal("video/quicktime", DysonVideoFiles.ContentTypeFor("a.mov"));
        Assert.Equal("video/x-matroska", DysonVideoFiles.ContentTypeFor("a.MKV"));

        Assert.Null(DysonVideoFiles.ContentTypeFor("a.ogv"));
        Assert.Null(DysonVideoFiles.ContentTypeFor("a.mp3"));
        Assert.Null(DysonVideoFiles.ContentTypeFor(""));
        Assert.Null(DysonVideoFiles.ContentTypeFor(null));

        Assert.True(DysonVideoFiles.IsVideoPath("clip.Mp4"));
        Assert.False(DysonVideoFiles.IsVideoPath("song.mp3"));
        Assert.False(DysonAudioFiles.IsAudioPath("a.mkv"));
    }

    [Fact]
    public async Task Video_file_streams_from_disk_and_close_revokes()
    {
        await using var ctx = await HostContext.CreateAsync();
        var workRoot = await NewWorkRootAsync(
            ("clip.MP4", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p']),
            ("movie.mkv", [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0]));
        try
        {
            var (opened, revealed) = RecordLaunchers(ctx.Host);

            foreach (var (name, mime) in new[] { ("clip.MP4", "video/mp4"), ("movie.mkv", "video/x-matroska") })
            {
                await ctx.Host.OpenFileViewerAsync(name, workRoot);

                var viewer = ctx.Host.FileViewer;
                Assert.NotNull(viewer);
                Assert.True(viewer.IsVideo);
                Assert.False(viewer.IsAudio);
                Assert.Null(viewer.Error);
                Assert.Equal("", viewer.Content);
                var previewId = viewer.AudioPreviewId;
                Assert.False(string.IsNullOrEmpty(previewId));
                Assert.Equal(DysonFilePreviewStore.UrlFor(previewId!), viewer.AudioPreviewUrl);
                Assert.True(ctx.Previews.TryGet(previewId!, out var entry));
                Assert.Equal(mime, entry.ContentType);
                Assert.Equal(Path.GetFullPath(Path.Combine(workRoot, name)), entry.FilePath);
                Assert.Empty(entry.Bytes);

                ctx.Host.CloseFileViewer();
                Assert.Null(ctx.Host.FileViewer);
                Assert.False(ctx.Previews.TryGet(previewId!, out _));
            }

            Assert.Empty(opened);
            Assert.Empty(revealed);
        }
        finally
        {
            DeleteQuietly(workRoot);
        }
    }

    private static string LongPlan(int minChars)
    {
        var sb = new StringBuilder("# Long plan\n\n");
        for (var i = 0; sb.Length < minChars; i++)
            sb.Append($"## {i}. Section\n\nSome **bold** and `code` text.\n\n| Col | Value |\n|---|---|\n| a{i} | **b** |\n\n");
        return sb.ToString();
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
                DysonTempDb.Plans(accessor),
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

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>PDF path / magic helpers, preview store, and file-viewer Git annotation hook.</summary>
public class DysonFileViewerPdfTests
{
    [Fact]
    public void Run()
    {
        if (!DysonFileViewerState.IsPdfPath("docs/a.PDF")
            || !DysonFileViewerState.IsPdfPath("x.pdf")
            || DysonFileViewerState.IsPdfPath("x.md")
            || DysonFileViewerState.IsPdfPath("pdf.txt"))
        {
            throw new InvalidOperationException("IsPdfPath extension mismatch.");
        }

        var magic = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        if (!DysonFileViewerState.LooksLikePdf(magic)
            || DysonFileViewerState.LooksLikePdf("%PDF"u8)
            || DysonFileViewerState.LooksLikePdf("hello"u8))
        {
            throw new InvalidOperationException("LooksLikePdf magic mismatch.");
        }

        var store = new DysonFilePreviewStore();
        var id = store.Put([1, 2, 3], "application/pdf");
        if (!store.TryGet(id, out var entry)
            || entry.ContentType != "application/pdf"
            || entry.Bytes is not [1, 2, 3]
            || DysonFilePreviewStore.UrlFor(id) != $"{DysonFilePreviewStore.RoutePrefix}/{id}")
        {
            throw new InvalidOperationException("Preview store put/get/url mismatch.");
        }

        store.Remove(id);
        if (store.TryGet(id, out _))
            throw new InvalidOperationException("Preview store must remove by id.");

        var state = new DysonFileViewerState
        {
            RelativePath = "a.txt",
            Title = "a.txt",
            Content = "hi",
            IsMarkdown = false,
        };
        if (state.GitDiffAnnotations.Count != 0)
            throw new InvalidOperationException("GitDiffAnnotations must default empty.");
    }

    [Fact]
    public async Task OpenFileViewer_attaches_git_hunks_only_for_workspace_text()
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
        await using var host = new DysonUiHost(
            sessions,
            models,
            workDirs,
            workDirConfigs,
            settings,
            shells,
            http,
            new DysonCliProxyHost(http),
            new DysonFilePreviewStore(),
            catalog,
            contributions,
            grantService,
            mcpResolver,
            lifecycle,
            new ThemeService(new ThemeJsRuntime("light", "#ABC")));

        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-fv-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workRoot, "notes.txt"), "hello");
            await File.WriteAllBytesAsync(Path.Combine(workRoot, "doc.pdf"), "%PDF-1.4\n"u8.ToArray());

            var actions = new[]
            {
                new DysonFileViewerAction
                {
                    Label = "Keep me",
                    Invoke = static () => Task.CompletedTask,
                    IsPrimary = true,
                },
            };
            await host.OpenFileViewerAsync("notes.txt", workRoot, actions);
            Assert.NotNull(host.FileViewer);
            Assert.Equal("hello", host.FileViewer.Content);
            Assert.Null(host.FileViewer.Error);
            Assert.False(host.FileViewer.IsPdf);
            Assert.False(host.FileViewer.IsImage);
            Assert.Empty(host.FileViewer.GitDiffAnnotations);
            Assert.Single(host.FileViewer.Actions);
            Assert.Equal("Keep me", host.FileViewer.Actions[0].Label);

            host.OpenFileViewerContent("skillsdirectory:demo/SKILL.md", "# Skill");
            Assert.NotNull(host.FileViewer);
            Assert.Equal("# Skill", host.FileViewer.Content);
            Assert.Null(host.FileViewer.Error);
            Assert.Empty(host.FileViewer.GitDiffAnnotations);

            await host.OpenFileViewerAsync("doc.pdf", workRoot);
            Assert.NotNull(host.FileViewer);
            Assert.True(host.FileViewer.IsPdf);
            Assert.Null(host.FileViewer.Error);
            Assert.False(string.IsNullOrEmpty(host.FileViewer.PdfPreviewUrl));
            Assert.Empty(host.FileViewer.GitDiffAnnotations);

            host.OpenPendingImageViewer(new PendingComposerImage(
                Guid.NewGuid(),
                "shot.jpg",
                "image/jpeg",
                Convert.ToBase64String([1, 2, 3]),
                ".jpg"));
            Assert.NotNull(host.FileViewer);
            Assert.True(host.FileViewer.IsImage);
            Assert.Null(host.FileViewer.Error);
            Assert.Empty(host.FileViewer.GitDiffAnnotations);

            GitInit(workRoot);
            await File.WriteAllTextAsync(Path.Combine(workRoot, "untracked.txt"), "one\ntwo\n");
            await host.OpenFileViewerAsync("untracked.txt", workRoot);
            Assert.NotNull(host.FileViewer);
            Assert.Equal("one\ntwo\n", host.FileViewer.Content);
            Assert.Null(host.FileViewer.Error);
            Assert.NotEmpty(host.FileViewer.GitDiffAnnotations);
            Assert.Equal(DysonGitDiffAnnotationKind.Added, host.FileViewer.GitDiffAnnotations[0].Kind);
        }
        finally
        {
            try
            {
                Directory.Delete(workRoot, recursive: true);
            }
            catch
            {
                // ignore cleanup races
            }
        }
    }

    private static void GitInit(string root)
    {
        RunGitOrThrow(root, ["init"]);
        RunGitOrThrow(root, ["config", "user.email", "test@example.com"]);
        RunGitOrThrow(root, ["config", "user.name", "test"]);
    }

    private static void RunGitOrThrow(string workingDirectory, string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start())
            throw new InvalidOperationException("Failed to start git.");
        if (!process.WaitForExit(10_000))
            throw new InvalidOperationException("git timed out.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
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

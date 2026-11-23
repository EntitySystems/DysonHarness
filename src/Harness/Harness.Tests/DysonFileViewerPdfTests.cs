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
            DysonTempDb.Plans(accessor),
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

            await host.OpenFileViewerContentAsync("skillsdirectory:demo/SKILL.md", "# Skill");
            Assert.NotNull(host.FileViewer);
            Assert.Equal("# Skill", host.FileViewer.Content);
            Assert.False(host.FileViewer.IsLoading);
            Assert.False(host.FileViewer.IsTextPreview);
            Assert.NotEmpty(host.FileViewer.MarkdownBlocks);
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

            using (var zipStream = File.Create(Path.Combine(workRoot, "pack.zip")))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var zipEntry = archive.CreateEntry("src/a.txt");
                await using var entryStream = zipEntry.Open();
                await entryStream.WriteAsync("hi"u8.ToArray());
            }

            await File.WriteAllTextAsync(Path.Combine(workRoot, "bad.zip"), "definitely not a zip");

            await host.OpenFileViewerAsync("pack.zip", workRoot);
            Assert.NotNull(host.FileViewer);
            Assert.True(host.FileViewer.IsZip);
            Assert.NotEmpty(host.FileViewer.ZipEntries);
            Assert.Equal("", host.FileViewer.Content);
            Assert.Null(host.FileViewer.Error);

            await host.OpenFileViewerAsync("bad.zip", workRoot);
            Assert.NotNull(host.FileViewer);
            Assert.True(host.FileViewer.IsZip);
            Assert.False(string.IsNullOrEmpty(host.FileViewer.Error));
            Assert.Empty(host.FileViewer.ZipEntries);

            // Nested zip opens at its own root on a fresh epoch; Back restores the entry's folder for every type.
            byte[] innerBytes;
            using (var innerStream = new MemoryStream())
            {
                using (var inner = new System.IO.Compression.ZipArchive(innerStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using var s = inner.CreateEntry("deep/x.txt").Open();
                    await s.WriteAsync("x"u8.ToArray());
                }
                innerBytes = innerStream.ToArray();
            }

            using (var zipStream = File.Create(Path.Combine(workRoot, "outer.zip")))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                await using (var s = archive.CreateEntry("sub/inner.zip").Open())
                    await s.WriteAsync(innerBytes);
                await using (var s = archive.CreateEntry("sub/note.txt").Open())
                    await s.WriteAsync("note"u8.ToArray());
            }

            var epochs = new List<int>();
            using var epochSub = host.Bus.Subscribe<DysonFileViewerChangedEvent>(host.BusScopeKey, e => epochs.Add(e.Epoch)).Value;
            var cacheDirs = new List<string>();
            try
            {
                await host.OpenFileViewerAsync("outer.zip", workRoot);
                Assert.Equal("", host.FileViewer!.ZipFolder);
                var outerEpoch = epochs[^1];

                await host.OpenZipEntryAsync("sub/inner.zip");
                Assert.True(host.FileViewer!.IsZip);
                Assert.Equal("inner.zip", host.FileViewer.RelativePath);
                Assert.Equal("", host.FileViewer.ZipFolder);
                Assert.NotEqual(outerEpoch, epochs[^1]);
                cacheDirs.Add(host.FileViewer.ZipWorkRoot!);
                Assert.Equal("Back to outer.zip", Assert.Single(host.FileViewer.Actions).Label);

                await host.FileViewer.Actions[0].Invoke();
                Assert.Equal("outer.zip", host.FileViewer!.RelativePath);
                Assert.Equal("sub", host.FileViewer.ZipFolder);

                await host.OpenZipEntryAsync("sub/note.txt");
                Assert.Equal("note", host.FileViewer!.Content);
                cacheDirs.Add(Path.GetDirectoryName(host.FileViewer.AbsolutePath!)!);
                await host.FileViewer.Actions[0].Invoke();
                Assert.True(host.FileViewer!.IsZip);
                Assert.Equal("sub", host.FileViewer.ZipFolder);

                await host.OpenFileViewerAsync("pack.zip", workRoot);
                Assert.Equal("", host.FileViewer!.ZipFolder);

                // Executables never reach the launcher: a binary .exe is refused (error for the banner, listing stays);
                // a text script opens read-only with no "Open in default editor".
                using (var zipStream = File.Create(Path.Combine(workRoot, "tools.zip")))
                using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
                {
                    await using (var s = archive.CreateEntry("bin/evil.exe").Open())
                        await s.WriteAsync(new byte[] { (byte)'M', (byte)'Z', 0, 0 });
                    await using (var s = archive.CreateEntry("run.sh").Open())
                        await s.WriteAsync("#!/bin/sh\necho hi\n"u8.ToArray());
                }

                await host.OpenFileViewerAsync("tools.zip", workRoot);
                var toolsViewer = host.FileViewer;
                var refused = await host.OpenZipEntryAsync("bin/evil.exe");
                Assert.True(refused.IsError);
                Assert.StartsWith("Refusing to open 'evil.exe'", refused.Error);
                Assert.Same(toolsViewer, host.FileViewer);

                var script = await host.OpenZipEntryAsync("run.sh");
                Assert.True(script.IsSuccess);
                Assert.Equal("#!/bin/sh\necho hi\n", host.FileViewer!.Content);
                Assert.False(host.FileViewer.CanOpenInDefaultEditor);
                Assert.Equal("Back to tools.zip", Assert.Single(host.FileViewer.Actions).Label);
                cacheDirs.Add(Path.GetDirectoryName(host.FileViewer.AbsolutePath!)!);

                // Magic bytes beat the extension; video entries open in the player; other binaries go to the OS
                // default handler (never the reveal path, which is for workspace executables only).
                var opened = new List<string>();
                var revealed = new List<string>();
                host.OpenWithOsDefault = p => { opened.Add(p); return Task.FromResult(VoidResult<string>.Success); };
                host.RevealInFileManager = p => { revealed.Add(p); return Task.FromResult(VoidResult<string>.Success); };
                using (var zipStream = File.Create(Path.Combine(workRoot, "media.zip")))
                using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
                {
                    await using (var s = archive.CreateEntry("renamed.dat").Open())
                        await s.WriteAsync(new byte[] { (byte)'M', (byte)'Z', 0x90, 0 });
                    await using (var s = archive.CreateEntry("clip.mp4").Open())
                        await s.WriteAsync(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
                    await using (var s = archive.CreateEntry("art.psd").Open())
                        await s.WriteAsync(new byte[] { (byte)'8', (byte)'B', (byte)'P', (byte)'S', 0, 1, 0, 0 });
                }

                await host.OpenFileViewerAsync("media.zip", workRoot);
                var mediaViewer = host.FileViewer;
                var renamed = await host.OpenZipEntryAsync("renamed.dat");
                Assert.True(renamed.IsError);
                Assert.StartsWith("Refusing to open 'renamed.dat'", renamed.Error);
                Assert.Same(mediaViewer, host.FileViewer);

                var clip = await host.OpenZipEntryAsync("clip.mp4");
                Assert.True(clip.IsSuccess);
                Assert.True(host.FileViewer!.IsVideo);
                Assert.False(host.FileViewer.IsAudio);
                Assert.Equal("Back to media.zip", Assert.Single(host.FileViewer.Actions).Label);
                cacheDirs.Add(Path.GetDirectoryName(host.FileViewer.AbsolutePath!)!);

                await host.OpenFileViewerAsync("media.zip", workRoot);
                var psd = await host.OpenZipEntryAsync("art.psd");
                Assert.True(psd.IsSuccess);
                Assert.EndsWith("art.psd", Assert.Single(opened));
                Assert.Empty(revealed);
                Assert.True(host.FileViewer!.IsZip);

                // A late extraction must not resurrect a closed viewer or replace the file opened since.
                await host.OpenFileViewerAsync("tools.zip", workRoot);
                var late = host.OpenZipEntryAsync("run.sh");
                host.CloseFileViewer();
                await host.OpenFileViewerAsync("notes.txt", workRoot);
                Assert.True((await late).IsSuccess);
                Assert.Equal("notes.txt", host.FileViewer!.RelativePath);
            }
            finally
            {
                foreach (var cacheDir in cacheDirs)
                    try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
            }

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

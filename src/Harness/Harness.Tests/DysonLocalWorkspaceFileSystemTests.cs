using System.Diagnostics;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Local workspace FS: init subject, sandbox, enumerate, round-trip, watcher.</summary>
public class DysonLocalWorkspaceFileSystemTests
{
    [Fact]
    public async Task Initialize_local_fs_required_and_wrong_subject_rejected()
    {
        var root = CreateTempDir();
        try
        {
            var fs = new DysonLocalWorkspaceFileSystem(root);
            Assert.False(fs.IsInitialized);
            Assert.Null(fs.SubjectId);
            Assert.Equal(Path.GetFullPath(root), fs.NativeRootPath);

            var before = await fs.ReadAllTextAsync("a.txt");
            Assert.True(before.IsError);
            Assert.Contains("not initialized", before.Error, StringComparison.OrdinalIgnoreCase);

            var wrong = await fs.InitializeAsync("azure_files");
            Assert.True(wrong.IsError);
            Assert.Contains(DysonWorkspaceSubjects.LocalFs, wrong.Error, StringComparison.Ordinal);

            var ok = await fs.InitializeAsync(DysonWorkspaceSubjects.LocalFs);
            Assert.True(ok.IsSuccess, ok.IsError ? ok.Error : null);
            Assert.True(fs.IsInitialized);
            Assert.Equal(DysonWorkspaceSubjects.LocalFs, fs.SubjectId);

            var again = await fs.InitializeAsync(DysonWorkspaceSubjects.LocalFs);
            Assert.True(again.IsSuccess, again.IsError ? again.Error : null);

            var change = await fs.InitializeAsync("other");
            Assert.True(change.IsError);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task CreateLocalAsync_sandbox_enumerate_and_text_roundtrip()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            var escape = await fs.ReadAllTextAsync("../outside.txt");
            Assert.True(escape.IsError);
            Assert.Contains("escapes", escape.Error, StringComparison.OrdinalIgnoreCase);

            var write = await fs.WriteAllTextAsync("sub/hello.txt", "hello-world");
            Assert.True(write.IsSuccess, write.IsError ? write.Error : null);

            var read = await fs.ReadAllTextAsync("sub/hello.txt");
            Assert.True(read.IsSuccess, read.IsError ? read.Error : null);
            Assert.Equal("hello-world", read.Value);

            var entries = await fs.EnumerateEntriesAsync(".");
            Assert.True(entries.IsSuccess, entries.IsError ? entries.Error : null);
            Assert.Contains(entries.Value, e => e.IsDirectory && e.Name == "sub");

            var rel = fs.GetRelativePath(Path.Combine(root, "sub", "hello.txt"));
            Assert.True(rel.IsSuccess, rel.IsError ? rel.Error : null);
            Assert.Equal("sub/hello.txt", rel.Value.Replace('\\', '/'));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Watcher_fires_on_write()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            var watcherResult = fs.CreateWatcher();
            Assert.True(watcherResult.IsSuccess, watcherResult.IsError ? watcherResult.Error : null);
            using var watcher = watcherResult.Value;

            var tcs = new TaskCompletionSource<DysonWorkspaceChangeEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += (_, args) =>
            {
                if (args.Kind is DysonWorkspaceChangeKind.Created or DysonWorkspaceChangeKind.Changed)
                    tcs.TrySetResult(args);
            };

            var started = watcher.Start();
            Assert.True(started.IsSuccess, started.IsError ? started.Error : null);

            // Brief settle so the OS watcher attaches before the write.
            await Task.Delay(100);
            var write = await fs.WriteAllTextAsync("watched.txt", "ping");
            Assert.True(write.IsSuccess, write.IsError ? write.Error : null);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(tcs.Task, completed);
            var args = await tcs.Task;
            Assert.Contains("watched.txt", args.FullPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Move_renames_directory_rejects_collision_and_sandbox_escape()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            Assert.True((await fs.CreateDirectoryAsync("alpha")).IsSuccess);
            Assert.True((await fs.WriteAllTextAsync("alpha/note.txt", "hi")).IsSuccess);
            Assert.True((await fs.CreateDirectoryAsync("beta")).IsSuccess);

            var renamed = await fs.MoveAsync("alpha", "gamma");
            Assert.True(renamed.IsSuccess, renamed.IsError ? renamed.Error : null);
            Assert.True((await fs.DirectoryExistsAsync("gamma")).Value);
            Assert.False((await fs.DirectoryExistsAsync("alpha")).Value);
            Assert.Equal("hi", (await fs.ReadAllTextAsync("gamma/note.txt")).Value);

            var collision = await fs.MoveAsync("gamma", "beta");
            Assert.True(collision.IsError);
            Assert.Contains("already exists", collision.Error, StringComparison.OrdinalIgnoreCase);

            var intoSelf = await fs.MoveAsync("gamma", "gamma/nested");
            Assert.True(intoSelf.IsError);
            Assert.Contains("into itself", intoSelf.Error, StringComparison.OrdinalIgnoreCase);

            var escape = await fs.MoveAsync("gamma", "../outside");
            Assert.True(escape.IsError);
            Assert.Contains("escapes", escape.Error, StringComparison.OrdinalIgnoreCase);

            var fileMove = await fs.MoveAsync("gamma/note.txt", "beta/note.txt");
            Assert.True(fileMove.IsSuccess, fileMove.IsError ? fileMove.Error : null);
            Assert.True((await fs.FileExistsAsync("beta/note.txt")).Value);
            Assert.False((await fs.FileExistsAsync("gamma/note.txt")).Value);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReadLineSlice_skip_and_maxLines()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            var body = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line-{i}"));
            Assert.True((await fs.WriteAllTextAsync("lines.txt", body)).IsSuccess);

            var slice = await fs.ReadLineSliceAsync("lines.txt", startLine: 10, maxLines: 3, maxChars: 1_000_000, maxLineChars: 1_000_000);
            Assert.True(slice.IsSuccess, slice.IsError ? slice.Error : null);
            Assert.Equal(3, slice.Value.Lines.Count);
            Assert.Equal(10, slice.Value.StartLine);
            Assert.Equal(13, slice.Value.NextLine);
            Assert.True(slice.Value.Truncated);
            Assert.False(slice.Value.Tailed);
            Assert.Equal(new[] { 10, 11, 12 }, slice.Value.Lines.Select(l => l.LineNumber));
            Assert.Equal(new[] { "line-10", "line-11", "line-12" }, slice.Value.Lines.Select(l => l.Text));
            Assert.All(slice.Value.Lines, l => Assert.False(l.Clipped));
            Assert.True(slice.Value.FileLengthBytes > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReadLineSlice_maxChars_truncates()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            var body = string.Join('\n', Enumerable.Repeat("xx", 10));
            Assert.True((await fs.WriteAllTextAsync("short.txt", body)).IsSuccess);

            var slice = await fs.ReadLineSliceAsync("short.txt", startLine: 1, maxLines: 20, maxChars: 5, maxLineChars: 1000);
            Assert.True(slice.IsSuccess, slice.IsError ? slice.Error : null);
            Assert.True(slice.Value.Truncated);
            Assert.False(slice.Value.Tailed);
            var raw = slice.Value.Lines.Sum(l => l.Text.Length);
            Assert.True(raw <= 5, $"collected raw length {raw} exceeded maxChars");
            Assert.Equal(2, slice.Value.Lines.Count);
            Assert.Equal(new[] { "xx", "xx" }, slice.Value.Lines.Select(l => l.Text));
            Assert.Equal(3, slice.Value.NextLine);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReadLineSlice_tail()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            var body = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line-{i}"));
            Assert.True((await fs.WriteAllTextAsync("tail.txt", body)).IsSuccess);

            var slice = await fs.ReadLineSliceAsync("tail.txt", startLine: -2, maxLines: null, maxChars: 1_000_000, maxLineChars: 1_000_000);
            Assert.True(slice.IsSuccess, slice.IsError ? slice.Error : null);
            Assert.True(slice.Value.Tailed);
            Assert.False(slice.Value.Truncated);
            Assert.Equal(9, slice.Value.StartLine);
            Assert.Equal(11, slice.Value.NextLine);
            Assert.Equal(2, slice.Value.Lines.Count);
            Assert.Equal(new[] { 9, 10 }, slice.Value.Lines.Select(l => l.LineNumber));
            Assert.Equal(new[] { "line-9", "line-10" }, slice.Value.Lines.Select(l => l.Text));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReadLineSlice_giant_line_clips()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            Assert.True((await fs.WriteAllTextAsync("giant.txt", new string('A', 200_000))).IsSuccess);

            var sw = Stopwatch.StartNew();
            var slice = await fs.ReadLineSliceAsync("giant.txt", startLine: 1, maxLines: 10, maxChars: 1_000_000, maxLineChars: 8192);
            sw.Stop();

            Assert.True(slice.IsSuccess, slice.IsError ? slice.Error : null);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"giant-line clip took {sw.Elapsed}");
            Assert.Single(slice.Value.Lines);
            Assert.Equal(8192, slice.Value.Lines[0].Text.Length);
            Assert.True(slice.Value.Lines[0].Clipped);
            Assert.Equal(1, slice.Value.Lines[0].LineNumber);
            Assert.False(slice.Value.Tailed);
            Assert.True(slice.Value.FileLengthBytes >= 200_000);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReadLineSlice_missing_file()
    {
        var root = CreateTempDir();
        try
        {
            var created = await DysonWorkspaceFileSystems.CreateLocalAsync(root);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var fs = created.Value;

            var slice = await fs.ReadLineSliceAsync("missing.txt", startLine: 1, maxLines: 10, maxChars: 1000, maxLineChars: 1000);
            Assert.True(slice.IsError);
            Assert.Contains("not found", slice.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-wsfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore cleanup races with watcher
        }
    }
}

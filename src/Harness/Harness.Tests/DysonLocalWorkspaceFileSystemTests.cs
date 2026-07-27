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

            var before = fs.ReadAllText("a.txt");
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

            var escape = fs.ReadAllText("../outside.txt");
            Assert.True(escape.IsError);
            Assert.Contains("escapes", escape.Error, StringComparison.OrdinalIgnoreCase);

            var write = fs.WriteAllText("sub/hello.txt", "hello-world");
            Assert.True(write.IsSuccess, write.IsError ? write.Error : null);

            var read = fs.ReadAllText("sub/hello.txt");
            Assert.True(read.IsSuccess, read.IsError ? read.Error : null);
            Assert.Equal("hello-world", read.Value);

            var entries = fs.EnumerateEntries(".");
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
            var write = fs.WriteAllText("watched.txt", "ping");
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

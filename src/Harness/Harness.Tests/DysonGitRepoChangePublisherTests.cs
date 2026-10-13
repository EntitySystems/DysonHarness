using DysonHarness;

namespace Harness.Tests;

public class DysonGitRepoChangePublisherTests
{
    [Fact]
    public async Task Many_Changed_events_coalesce_to_one_bus_publish_after_delay()
    {
        var watchers = new List<FakeWatcher>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new DysonMessageBus();
        using var publisher = CreatePublisher(bus, watchers, gate);
        var received = new List<DysonGitRepoChangedEvent>();
        Assert.True(bus.Subscribe<DysonGitRepoChangedEvent>(DysonBusScopes.Wildcard, received.Add).IsSuccess);

        var id = Guid.NewGuid();
        var root = UniqueRoot("coalesce");
        Assert.True(publisher.Watch(id, root).IsSuccess);
        var watcher = Assert.Single(watchers);

        watcher.RaiseChanged();
        watcher.RaiseChanged();
        watcher.RaiseChanged();
        Assert.Empty(received);

        Assert.True(gate.TrySetResult());
        await WaitUntilAsync(() => received.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Single(received);
    }

    [Fact]
    public async Task Publish_key_is_WorkDirectory_scope_and_payload_matches_Watch_args()
    {
        var watchers = new List<FakeWatcher>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new DysonMessageBus();
        using var publisher = CreatePublisher(bus, watchers, gate);
        var id = Guid.NewGuid();
        var root = UniqueRoot("scope");
        var scope = DysonBusScopes.WorkDirectory(id);
        Assert.Equal($"workdir:{id:D}", scope);

        var wildcard = new List<DysonGitRepoChangedEvent>();
        var exact = new List<DysonGitRepoChangedEvent>();
        var other = new List<DysonGitRepoChangedEvent>();
        Assert.True(bus.Subscribe<DysonGitRepoChangedEvent>(DysonBusScopes.Wildcard, wildcard.Add).IsSuccess);
        Assert.True(bus.Subscribe<DysonGitRepoChangedEvent>(scope, exact.Add).IsSuccess);
        Assert.True(bus.Subscribe<DysonGitRepoChangedEvent>(
            DysonBusScopes.WorkDirectory(Guid.NewGuid()), other.Add).IsSuccess);

        Assert.True(publisher.Watch(id, root).IsSuccess);
        Assert.Single(watchers).RaiseChanged();
        Assert.True(gate.TrySetResult());
        await WaitUntilAsync(() => exact.Count == 1 && wildcard.Count == 1, TimeSpan.FromSeconds(2));

        Assert.Empty(other);
        Assert.Equal(id, exact[0].WorkDirectoryId);
        Assert.Equal(root, exact[0].RepoRoot);
        Assert.Equal(exact[0], wildcard[0]);
    }

    [Fact]
    public void Watch_on_new_root_disposes_previous_watcher()
    {
        var watchers = new List<FakeWatcher>();
        using var bus = new DysonMessageBus();
        using var publisher = CreatePublisher(bus, watchers, delay: HangDelay);
        var id = Guid.NewGuid();

        Assert.True(publisher.Watch(id, UniqueRoot("first")).IsSuccess);
        Assert.True(publisher.Watch(id, UniqueRoot("second")).IsSuccess);

        Assert.Equal(2, watchers.Count);
        Assert.True(watchers[0].Disposed);
        Assert.False(watchers[1].Disposed);
        Assert.True(watchers[1].Started);
    }

    [Fact]
    public async Task Unwatch_suppresses_pending_publish()
    {
        var watchers = new List<FakeWatcher>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new DysonMessageBus();
        using var publisher = CreatePublisher(bus, watchers, gate);
        var received = new List<DysonGitRepoChangedEvent>();
        Assert.True(bus.Subscribe<DysonGitRepoChangedEvent>(DysonBusScopes.Wildcard, received.Add).IsSuccess);

        Assert.True(publisher.Watch(Guid.NewGuid(), UniqueRoot("unwatch")).IsSuccess);
        Assert.Single(watchers).RaiseChanged();
        publisher.Unwatch();
        Assert.True(watchers[0].Disposed);

        Assert.True(gate.TrySetResult());
        await Task.Delay(50);
        Assert.Empty(received);
    }

    [Fact]
    public async Task Dispose_suppresses_pending_publish()
    {
        var watchers = new List<FakeWatcher>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new DysonMessageBus();
        var publisher = CreatePublisher(bus, watchers, gate);
        var received = new List<DysonGitRepoChangedEvent>();
        Assert.True(bus.Subscribe<DysonGitRepoChangedEvent>(DysonBusScopes.Wildcard, received.Add).IsSuccess);

        Assert.True(publisher.Watch(Guid.NewGuid(), UniqueRoot("dispose")).IsSuccess);
        Assert.Single(watchers).RaiseChanged();
        publisher.Dispose();
        Assert.True(watchers[0].Disposed);

        Assert.True(gate.TrySetResult());
        await Task.Delay(50);
        Assert.Empty(received);
    }

    [Fact]
    public async Task Failed_schedules_coalesced_event()
    {
        var watchers = new List<FakeWatcher>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bus = new DysonMessageBus();
        using var publisher = CreatePublisher(bus, watchers, gate);
        var received = new List<DysonGitRepoChangedEvent>();
        Assert.True(bus.Subscribe<DysonGitRepoChangedEvent>(DysonBusScopes.Wildcard, received.Add).IsSuccess);

        var id = Guid.NewGuid();
        var root = UniqueRoot("failed");
        Assert.True(publisher.Watch(id, root).IsSuccess);
        Assert.Single(watchers).RaiseFailed();
        Assert.Empty(received);

        Assert.True(gate.TrySetResult());
        await WaitUntilAsync(() => received.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(id, received[0].WorkDirectoryId);
        Assert.Equal(root, received[0].RepoRoot);
    }

    [Theory]
    [InlineData(@"E:\repo\.git\index.lock")]
    [InlineData("/repo/.git/HEAD.lock")]
    [InlineData("/repo/.git/packed-refs.lock")]
    [InlineData("/repo/.git/config.lock")]
    [InlineData("/repo/.git/refs/heads/main.lock")]
    [InlineData(@"E:\repo\.git\worktrees\abc\index.lock")]
    public void IsGitLockFilePath_true_for_lock_files_under_dot_git(string path) =>
        Assert.True(DysonGitRepoChangePublisher.IsGitLockFilePath(path));

    [Theory]
    [InlineData(@"E:\repo\.git\index")]
    [InlineData("/repo/.git/HEAD")]
    [InlineData("/repo/.git/ORIG_HEAD")]
    [InlineData("/repo/.git/refs/heads/main")]
    [InlineData("/repo/.github/workflows/x.lock")]
    [InlineData("/repo/src/package.lock")]
    [InlineData("/repo/.gitkeep")]
    [InlineData("/repo/.git.lock")]
    [InlineData("changed")]
    [InlineData("")]
    [InlineData(null)]
    public void IsGitLockFilePath_false_for_repo_state_and_non_git_paths(string? path) =>
        Assert.False(DysonGitRepoChangePublisher.IsGitLockFilePath(path));

    [Fact]
    public void Git_lock_file_events_do_not_schedule_publish_but_index_rename_does()
    {
        var watchers = new List<FakeWatcher>();
        var delays = 0;
        using var bus = new DysonMessageBus();
        using var publisher = CreatePublisher(bus, watchers, (_, ct) =>
        {
            Interlocked.Increment(ref delays);
            return Task.Delay(Timeout.Infinite, ct);
        });
        Assert.True(publisher.Watch(Guid.NewGuid(), UniqueRoot("lock")).IsSuccess);
        var watcher = Assert.Single(watchers);

        watcher.RaiseChanged(@"E:\repo\.git\index.lock");
        watcher.RaiseChanged("/repo/.git/worktrees/abc/index.lock");
        Assert.Equal(0, Volatile.Read(ref delays));

        watcher.RaiseChanged(@"E:\repo\.git\index", oldFullPath: @"E:\repo\.git\index.lock");
        Assert.Equal(1, Volatile.Read(ref delays));

        watcher.RaiseChanged();
        Assert.Equal(2, Volatile.Read(ref delays));
    }

    private static DysonGitRepoChangePublisher CreatePublisher(
        DysonMessageBus bus,
        List<FakeWatcher> watchers,
        TaskCompletionSource gate) =>
        CreatePublisher(bus, watchers, (_, ct) => gate.Task.WaitAsync(ct));

    private static DysonGitRepoChangePublisher CreatePublisher(
        DysonMessageBus bus,
        List<FakeWatcher> watchers,
        Func<int, CancellationToken, Task> delay) =>
        new(
            bus,
            _ =>
            {
                var watcher = new FakeWatcher();
                watchers.Add(watcher);
                return watcher;
            },
            delay);

    private static string UniqueRoot(string label) =>
        Path.Combine(Path.GetTempPath(), $"dyson-git-{label}-{Guid.NewGuid():N}");

    private static Task HangDelay(int _, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !predicate())
            await Task.Delay(10);

        Assert.True(predicate());
    }

    private sealed class FakeWatcher : IDysonWorkspaceChangeWatcher
    {
        public event EventHandler<DysonWorkspaceChangeEventArgs>? Changed;
        public event EventHandler<Exception>? Failed;

        public bool Disposed { get; private set; }
        public bool Started { get; private set; }

        public VoidResult<string> Start()
        {
            Started = true;
            return VoidResult<string>.Success;
        }

        public void Stop()
        {
        }

        public void Dispose() => Disposed = true;

        public void RaiseChanged(string fullPath = "changed", string? oldFullPath = null) =>
            Changed?.Invoke(
                this,
                new DysonWorkspaceChangeEventArgs
                {
                    Kind = oldFullPath is null ? DysonWorkspaceChangeKind.Changed : DysonWorkspaceChangeKind.Renamed,
                    FullPath = fullPath,
                    OldFullPath = oldFullPath,
                });

        public void RaiseFailed() => Failed?.Invoke(this, new IOException("overflow"));
    }
}

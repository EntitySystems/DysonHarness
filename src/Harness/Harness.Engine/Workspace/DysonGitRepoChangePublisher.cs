namespace DysonHarness;

/// <summary>
/// Engine-owned watcher on a git repo root. Trailing-debounces filesystem noise, then publishes
/// <see cref="DysonGitRepoChangedEvent"/> on <see cref="DysonBusScopes.WorkDirectory"/>.
/// </summary>
public sealed class DysonGitRepoChangePublisher : IDisposable
{
    public const int DebounceMs = 1000;

    private readonly DysonMessageBus _bus;
    private readonly Func<string, IDysonWorkspaceChangeWatcher> _createWatcher;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private IDysonWorkspaceChangeWatcher? _watcher;
    private CancellationTokenSource? _pendingCts;
    private Guid _workDirectoryId;
    private string? _repoRoot;
    private string? _fullPath;
    private bool _disposed;

    public DysonGitRepoChangePublisher(DysonMessageBus bus)
        : this(bus, createWatcher: null, delay: null)
    {
    }

    public DysonGitRepoChangePublisher(
        DysonMessageBus bus,
        Func<string, IDysonWorkspaceChangeWatcher>? createWatcher,
        Func<int, CancellationToken, Task>? delay)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _createWatcher = createWatcher ?? (path => new DysonLocalWorkspaceChangeWatcher(path));
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public VoidResult<string> Watch(Guid workDirectoryId, string repoRoot)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrWhiteSpace(repoRoot))
                return VoidResult<string>.AsError("repo root is required");

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(repoRoot.Trim());
            }
            catch (Exception ex)
            {
                return VoidResult<string>.AsError($"Invalid repo root: {ex.Message}", ex);
            }

            if (_watcher is not null
                && _workDirectoryId == workDirectoryId
                && PathsEqual(_fullPath, fullPath))
                return VoidResult<string>.Success;

            StopWatchingLocked();

            IDysonWorkspaceChangeWatcher watcher;
            try
            {
                watcher = _createWatcher(fullPath);
            }
            catch (Exception ex)
            {
                return VoidResult<string>.AsError($"File watcher unavailable: {ex.Message}", ex);
            }

            watcher.Changed += OnChanged;
            watcher.Failed += OnFailed;
            var started = watcher.Start();
            if (started.IsError)
            {
                watcher.Changed -= OnChanged;
                watcher.Failed -= OnFailed;
                watcher.Dispose();
                return started;
            }

            _watcher = watcher;
            _workDirectoryId = workDirectoryId;
            _repoRoot = repoRoot;
            _fullPath = fullPath;
            return VoidResult<string>.Success;
        }
    }

    public void Unwatch()
    {
        lock (_lock)
            StopWatchingLocked();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopWatchingLocked();
        }
    }

    private void OnChanged(object? sender, DysonWorkspaceChangeEventArgs e) => SchedulePublish(sender);

    private void OnFailed(object? sender, Exception exception)
    {
        // ponytail: no auto-restart on buffer overflow; upgrade = recreate watcher on Failed
        SchedulePublish(sender);
    }

    private void SchedulePublish(object? sender)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_disposed || _watcher is null || !ReferenceEquals(sender, _watcher))
                return;

            CancelPendingLocked();
            cts = new CancellationTokenSource();
            _pendingCts = cts;
        }

        _ = RunDebounceAsync(cts);
    }

    private async Task RunDebounceAsync(CancellationTokenSource cts)
    {
        try
        {
            await _delay(DebounceMs, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        Guid id;
        string repoRoot;
        lock (_lock)
        {
            if (_disposed || _watcher is null || !ReferenceEquals(_pendingCts, cts) || _repoRoot is null)
                return;

            _pendingCts = null;
            id = _workDirectoryId;
            repoRoot = _repoRoot;
        }

        cts.Dispose();
        _bus.Publish(DysonBusScopes.WorkDirectory(id), new DysonGitRepoChangedEvent(id, repoRoot));
    }

    private void StopWatchingLocked()
    {
        CancelPendingLocked();
        var watcher = _watcher;
        _watcher = null;
        _workDirectoryId = Guid.Empty;
        _repoRoot = null;
        _fullPath = null;
        if (watcher is null)
            return;

        watcher.Changed -= OnChanged;
        watcher.Failed -= OnFailed;
        watcher.Stop();
        watcher.Dispose();
    }

    private void CancelPendingLocked()
    {
        var cts = _pendingCts;
        _pendingCts = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private static bool PathsEqual(string? a, string b)
    {
        if (a is null)
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(a, b, comparison);
    }
}

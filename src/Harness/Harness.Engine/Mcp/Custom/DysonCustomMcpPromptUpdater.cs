namespace DysonHarness;

/// <summary>
/// Workdir-scoped state machine: Idle → Debouncing → Refreshing.
/// Driven by <see cref="FileSystemWatcher"/> on <c>.dyson/mcp</c> and mcpActive on/off.
/// </summary>
public sealed class DysonCustomMcpPromptUpdater : IAsyncDisposable
{
    private readonly DysonCustomMcpHost _host;
    private readonly object _gate = new();
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(300);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private bool _pendingDuringRefresh;
    private State _state = State.Idle;
    private int _disposed;

    public DysonCustomMcpPromptUpdater(DysonCustomMcpHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public enum State
    {
        Idle = 0,
        Debouncing = 1,
        Refreshing = 2,
    }

    public State CurrentState
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Starts the watcher (ensures <c>.dyson/mcp</c> exists). Safe to call repeatedly.</summary>
    public VoidResult<string> StartWatcher()
    {
        if (_disposed != 0)
            return new VoidResult<string>("Prompt updater is disposed.");

        var ensure = DysonCustomMcpConfigLoader.EnsureDirectory(_host.WorkRoot);
        if (ensure.IsError)
            return ensure;

        lock (_gate)
        {
            if (_watcher is not null)
                return VoidResult<string>.Success;

            try
            {
                var dir = DysonCustomMcpConfigLoader.GetDirectory(_host.WorkRoot);
                var watcher = new FileSystemWatcher(dir)
                {
                    Filter = "*.json",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.CreationTime
                        | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };

                watcher.Created += OnFsEvent;
                watcher.Changed += OnFsEvent;
                watcher.Deleted += OnFsEvent;
                watcher.Renamed += OnFsRenamed;
                watcher.Error += (_, _) => EnqueueRefresh();

                _watcher = watcher;
            }
            catch (Exception ex)
            {
                return new VoidResult<string>($"Failed to watch .dyson/mcp: {ex.Message}");
            }
        }

        return VoidResult<string>.Success;
    }

    public void NotifyMcpActiveChanged() => EnqueueRefresh();

    public void NotifyRestartRequested(string? serverId = null)
    {
        // Targeted reconnect still goes through the same refresh path (plan).
        _ = serverId;
        EnqueueRefresh();
    }

    /// <summary>Kick an immediate debounce cycle (also used after Retain).</summary>
    public void EnqueueRefresh()
    {
        if (_disposed != 0)
            return;

        lock (_gate)
        {
            if (_state == State.Refreshing)
            {
                _pendingDuringRefresh = true;
                return;
            }

            _state = State.Debouncing;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _ = DebounceThenRefreshAsync(cts.Token);
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => EnqueueRefresh();

    private void OnFsRenamed(object sender, RenamedEventArgs e) => EnqueueRefresh();

    private async Task DebounceThenRefreshAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunRefreshLoopAsync().ConfigureAwait(false);
    }

    private async Task RunRefreshLoopAsync()
    {
        while (_disposed == 0)
        {
            lock (_gate)
            {
                _state = State.Refreshing;
                _pendingDuringRefresh = false;
            }

            try
            {
                await _host.RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Log-ish: surface via a synthetic status on next GetStatuses if refresh itself fails hard.
                System.Diagnostics.Debug.WriteLine($"Custom MCP refresh failed: {ex.Message}");
            }

            bool again;
            lock (_gate)
            {
                again = _pendingDuringRefresh;
                if (again)
                {
                    _pendingDuringRefresh = false;
                }
                else
                {
                    _state = State.Idle;
                }
            }

            if (!again)
                return;

            // Brief coalesce before another refresh if events arrived mid-refresh.
            try
            {
                await Task.Delay(_debounce).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;

            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFsEvent;
                _watcher.Changed -= OnFsEvent;
                _watcher.Deleted -= OnFsEvent;
                _watcher.Renamed -= OnFsRenamed;
                _watcher.Dispose();
                _watcher = null;
            }

            _state = State.Idle;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

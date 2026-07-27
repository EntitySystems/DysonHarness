namespace DysonHarness;

/// <summary>
/// <see cref="FileSystemWatcher"/> wrapper over an initialized local workspace root.
/// </summary>
internal sealed class DysonLocalWorkspaceChangeWatcher : IDysonWorkspaceChangeWatcher
{
    private readonly string _root;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public DysonLocalWorkspaceChangeWatcher(string nativeRootPath)
    {
        _root = nativeRootPath;
    }

    public event EventHandler<DysonWorkspaceChangeEventArgs>? Changed;
    public event EventHandler<Exception>? Failed;

    public VoidResult<string> Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = true;
            return VoidResult<string>.Success;
        }

        try
        {
            var watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"File watcher unavailable: {ex.Message}", ex);
        }
    }

    public void Stop()
    {
        var watcher = _watcher;
        if (watcher is null)
            return;

        watcher.EnableRaisingEvents = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
            return;

        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnCreated;
        watcher.Changed -= OnChanged;
        watcher.Deleted -= OnDeleted;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Raise(DysonWorkspaceChangeKind.Created, e.FullPath, oldFullPath: null);

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Raise(DysonWorkspaceChangeKind.Changed, e.FullPath, oldFullPath: null);

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Raise(DysonWorkspaceChangeKind.Deleted, e.FullPath, oldFullPath: null);

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        Raise(DysonWorkspaceChangeKind.Renamed, e.FullPath, e.OldFullPath);

    private void OnError(object sender, ErrorEventArgs e) =>
        Failed?.Invoke(this, e.GetException());

    private void Raise(DysonWorkspaceChangeKind kind, string fullPath, string? oldFullPath) =>
        Changed?.Invoke(
            this,
            new DysonWorkspaceChangeEventArgs
            {
                Kind = kind,
                FullPath = fullPath,
                OldFullPath = oldFullPath,
            });
}

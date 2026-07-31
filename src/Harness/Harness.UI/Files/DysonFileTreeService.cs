using DysonHarness;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.UI.Files;

/// <summary>
/// Process-lifetime in-memory file tree: per-workdir cache, async directory skeleton,
/// lazy file load on expand, and workspace-FS watcher live updates.
/// </summary>
public sealed class DysonFileTreeService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<Guid, DysonFileTreeState> _cache = new();
    private readonly object _gate = new();
    private bool _disposed;

    public DysonFileTreeService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public DysonFileTreeState? Active { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Switch the active tree. Cached workdirs are reused (expand/load state preserved).
    /// Watchers for inactive caches stay alive until process shutdown.
    /// </summary>
    public async Task<VoidResult<string>> SetActiveAsync(
        Guid? workDirectoryId,
        string? subjectId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (workDirectoryId is null)
        {
            lock (_gate)
                Active = null;
            Notify();
            return VoidResult<string>.Success;
        }

        var id = workDirectoryId.Value;
        lock (_gate)
        {
            if (_cache.TryGetValue(id, out var cached))
            {
                Active = cached;
                Notify();
                return VoidResult<string>.Success;
            }
        }

        string absolutePath;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            DysonCloudSubjectScope.TryBind(scope.ServiceProvider, subjectId);
            var store = scope.ServiceProvider.GetRequiredService<IDysonWorkDirectoryRepository>();
            var get = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (get.IsError)
                return VoidResult<string>.AsError(get.Error);

            absolutePath = get.Value.AbsolutePath;
        }

        return await ActivateNewAsync(id, absolutePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Activate by known absolute path (skips store lookup). Used by tests.</summary>
    public VoidResult<string> SetActive(Guid workDirectoryId, string absolutePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        lock (_gate)
        {
            if (_cache.TryGetValue(workDirectoryId, out var cached))
            {
                Active = cached;
                Notify();
                return VoidResult<string>.Success;
            }
        }

        return ActivateNewAsync(workDirectoryId, absolutePath, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public async Task<VoidResult<string>> ExpandAsync(
        DysonFileTreeNode node,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsDirectory)
            return VoidResult<string>.AsError("Only directories can be expanded.");

        DysonFileTreeState? state;
        lock (_gate)
            state = Active;

        if (state is null)
            return VoidResult<string>.AsError("No active file tree.");

        if (node.ChildrenLoaded)
        {
            node.IsExpanded = true;
            Notify();
            return VoidResult<string>.Success;
        }

        node.IsLoading = true;
        Notify();

        try
        {
            var result = await Task.Run(
                    () => state.ShallowLoadChildren(node),
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsError)
            {
                node.IsLoading = false;
                Notify();
                return result;
            }

            node.IsExpanded = true;
            node.IsLoading = false;
            Notify();
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException)
        {
            node.IsLoading = false;
            Notify();
            throw;
        }
    }

    public void Collapse(DysonFileTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsExpanded)
            return;

        node.IsExpanded = false;
        Notify();
    }

    /// <summary>
    /// Force a shallow reload of a directory node (e.g. after clearing composer-uploads).
    /// No-op when the active tree has not loaded that path yet.
    /// </summary>
    public async Task InvalidateDirectoryAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        DysonFileTreeState? state;
        lock (_gate)
            state = Active;

        if (state is null)
            return;

        var normalized = relativePath.Replace('\\', '/').TrimEnd('/');
        var node = state.TryFindNode(normalized);
        if (node is null || !node.IsDirectory)
            return;

        node.Children.Clear();
        node.ChildrenLoaded = false;
        if (node.IsExpanded)
            await ExpandAsync(node, cancellationToken).ConfigureAwait(false);
        else
            Notify();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (_gate)
        {
            foreach (var state in _cache.Values)
                state.Dispose();
            _cache.Clear();
            Active = null;
        }
    }

    private async Task<VoidResult<string>> ActivateNewAsync(
        Guid id,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(absolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
            return VoidResult<string>.AsError(fsResult.Error);

        var state = new DysonFileTreeState(id, fsResult.Value, Notify);
        lock (_gate)
        {
            if (_cache.TryGetValue(id, out var raced))
            {
                state.Dispose();
                Active = raced;
                Notify();
                return VoidResult<string>.Success;
            }

            _cache[id] = state;
            Active = state;
        }

        Notify();
        state.StartSkeletonAndWatcher();
        return VoidResult<string>.Success;
    }

    private void Notify() => Changed?.Invoke();
}

public sealed class DysonFileTreeNode
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsExpanded { get; set; }
    public bool ChildrenLoaded { get; set; }
    public bool IsLoading { get; set; }
    public List<DysonFileTreeNode> Children { get; } = [];
}

/// <summary>Per-workdir tree + watcher. Survives workdir switches until process exit.</summary>
public sealed class DysonFileTreeState : IDisposable
{
    private const int DebounceMs = 120;

    private readonly IDysonWorkspaceFileSystem _fs;
    private readonly Action _notify;
    private readonly object _treeGate = new();
    private readonly object _pendingGate = new();
    private readonly List<PendingFsOp> _pending = [];
    private IDysonWorkspaceChangeWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _disposed;
    private bool _resyncScheduled;

    public DysonFileTreeState(Guid workDirectoryId, IDysonWorkspaceFileSystem workspaceFileSystem, Action notify)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileSystem);
        if (!workspaceFileSystem.IsInitialized)
            throw new ArgumentException("Workspace filesystem must be initialized.", nameof(workspaceFileSystem));

        WorkDirectoryId = workDirectoryId;
        _fs = workspaceFileSystem;
        AbsolutePath = _fs.NativeRootPath;
        FileSystem = _fs;
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));

        var name = new DirectoryInfo(AbsolutePath).Name;
        if (string.IsNullOrWhiteSpace(name))
            name = AbsolutePath;

        Root = new DysonFileTreeNode
        {
            Name = name,
            RelativePath = "",
            IsDirectory = true,
            IsExpanded = true,
            ChildrenLoaded = false,
        };
    }

    public Guid WorkDirectoryId { get; }
    public string AbsolutePath { get; }
    public IDysonWorkspaceFileSystem FileSystem { get; }
    public DysonFileTreeNode Root { get; }
    public bool SkeletonComplete { get; private set; }
    public bool SkeletonRunning { get; private set; }
    public string? Error { get; private set; }
    public bool Dirty { get; private set; }

    public void StartSkeletonAndWatcher()
    {
        StartWatcher();
        SkeletonRunning = true;
        _ = Task.Run(RunSkeleton);
    }

    /// <summary>Thread-safe copy for UI enumeration (skeleton/watcher mutate under the same lock).</summary>
    public DysonFileTreeNode[] SnapshotChildren(DysonFileTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_treeGate)
            return node.Children.Count == 0 ? [] : node.Children.ToArray();
    }

    public VoidResult<string> ShallowLoadChildren(DysonFileTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsDirectory)
            return VoidResult<string>.AsError("Not a directory.");

        var path = node.RelativePath.Length == 0 ? "." : node.RelativePath;
        var entries = _fs.EnumerateEntries(path);
        if (entries.IsError)
            return VoidResult<string>.AsError(entries.Error);

        lock (_treeGate)
        {
            var known = node.Children.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Value)
            {
                if (string.IsNullOrEmpty(entry.Name) || known.ContainsKey(entry.Name))
                    continue;

                var childRel = CombineRelative(node.RelativePath, entry.Name);
                var child = new DysonFileTreeNode
                {
                    Name = entry.Name,
                    RelativePath = childRel,
                    IsDirectory = entry.IsDirectory,
                };
                InsertSorted(node.Children, child);
                known[entry.Name] = child;
            }

            node.ChildrenLoaded = true;
            Dirty = false;
        }

        return VoidResult<string>.Success;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopWatcher();
        lock (_pendingGate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pending.Clear();
        }
    }

    private void RunSkeleton()
    {
        try
        {
            lock (_treeGate)
                WalkDirectories(Root);

            lock (_treeGate)
            {
                SkeletonComplete = true;
                SkeletonRunning = false;
            }

            // Root starts expanded: load its files after the directory skeleton attaches.
            if (Root.IsExpanded && !Root.ChildrenLoaded)
            {
                var load = ShallowLoadChildren(Root);
                if (load.IsError)
                    Error = load.Error;
            }
        }
        catch (Exception ex)
        {
            lock (_treeGate)
            {
                Error = $"Failed to build file tree: {ex.Message}";
                SkeletonRunning = false;
                SkeletonComplete = true;
            }
        }

        _notify();
    }

    private void WalkDirectories(DysonFileTreeNode parent)
    {
        var path = parent.RelativePath.Length == 0 ? "." : parent.RelativePath;
        var entries = _fs.EnumerateEntries(path);
        if (entries.IsError)
            return;

        var dirs = entries.Value
            .Where(e => e.IsDirectory && !string.IsNullOrEmpty(e.Name))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in dirs)
        {
            var existing = parent.Children.Find(c =>
                c.IsDirectory && c.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            DysonFileTreeNode child;
            if (existing is not null)
            {
                child = existing;
            }
            else
            {
                child = new DysonFileTreeNode
                {
                    Name = entry.Name,
                    RelativePath = CombineRelative(parent.RelativePath, entry.Name),
                    IsDirectory = true,
                };
                InsertSorted(parent.Children, child);
            }

            if (!IsNodeModulesName(entry.Name))
                WalkDirectories(child);
        }
    }

    private void StartWatcher()
    {
        try
        {
            var created = _fs.CreateWatcher();
            if (created.IsError)
            {
                Error = created.Error;
                _notify();
                return;
            }

            var watcher = created.Value;
            watcher.Changed += OnFsChanged;
            watcher.Failed += OnFsFailed;
            var started = watcher.Start();
            if (started.IsError)
            {
                watcher.Dispose();
                Error = started.Error;
                _notify();
                return;
            }

            _watcher = watcher;
        }
        catch (Exception ex)
        {
            Error = $"File watcher unavailable: {ex.Message}";
            _notify();
        }
    }

    private void StopWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
            return;

        watcher.Stop();
        watcher.Changed -= OnFsChanged;
        watcher.Failed -= OnFsFailed;
        watcher.Dispose();
    }

    private void OnFsChanged(object? sender, DysonWorkspaceChangeEventArgs e)
    {
        switch (e.Kind)
        {
            case DysonWorkspaceChangeKind.Created:
            case DysonWorkspaceChangeKind.Changed:
                Enqueue(new PendingFsOp(FsOpKind.Created, e.FullPath, OldFullPath: null));
                break;
            case DysonWorkspaceChangeKind.Deleted:
                Enqueue(new PendingFsOp(FsOpKind.Deleted, e.FullPath, OldFullPath: null));
                break;
            case DysonWorkspaceChangeKind.Renamed:
                Enqueue(new PendingFsOp(FsOpKind.Renamed, e.FullPath, e.OldFullPath));
                break;
        }
    }

    private void OnFsFailed(object? sender, Exception ex)
    {
        Dirty = true;
        // ponytail: global dirty + shallow resync of loaded dirs; upgrade = per-node dirty map
        ScheduleLoadedResync();
        _notify();
    }

    private void Enqueue(PendingFsOp op)
    {
        if (_disposed)
            return;

        lock (_pendingGate)
        {
            _pending.Add(op);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                _ => FlushPending(),
                null,
                DebounceMs,
                Timeout.Infinite);
        }
    }

    private void FlushPending()
    {
        List<PendingFsOp> batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0)
                return;
            batch = [.. _pending];
            _pending.Clear();
        }

        var changed = false;
        lock (_treeGate)
        {
            foreach (var op in batch)
            {
                switch (op.Kind)
                {
                    case FsOpKind.Created:
                        changed |= ApplyCreated(op.FullPath);
                        break;
                    case FsOpKind.Deleted:
                        changed |= ApplyDeleted(op.FullPath);
                        break;
                    case FsOpKind.Renamed:
                        if (op.OldFullPath is not null)
                            changed |= ApplyDeleted(op.OldFullPath);
                        changed |= ApplyCreated(op.FullPath);
                        break;
                }
            }
        }

        if (changed)
            _notify();
    }

    private bool ApplyCreated(string fullPath)
    {
        var rel = TryGetRelative(fullPath);
        if (rel is null || rel.Length == 0)
            return false;

        if (ShouldIgnoreWatcherPath(rel))
            return false;

        var parentRel = GetParentRelative(rel);
        var parent = FindNode(parentRel);
        if (parent is null || !parent.IsDirectory)
            return false;

        var isDirResult = _fs.DirectoryExists(rel);
        var isFileResult = _fs.FileExists(rel);
        if (isDirResult.IsError || isFileResult.IsError)
            return false;

        var isDir = isDirResult.Value;
        if (!isDir && !isFileResult.Value)
            return false;

        var inLazyZone = IsUnderNodeModulesSegment(rel);
        // Dirs outside an unexpanded node_modules interior join the skeleton even if files aren't loaded yet.
        if (!parent.ChildrenLoaded && !(isDir && !inLazyZone))
            return false;

        var name = Path.GetFileName(rel.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
            return false;

        if (parent.Children.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return false;

        var child = new DysonFileTreeNode
        {
            Name = name,
            RelativePath = rel,
            IsDirectory = isDir,
        };
        InsertSorted(parent.Children, child);
        return true;
    }

    private bool ApplyDeleted(string fullPath)
    {
        var rel = TryGetRelative(fullPath);
        if (rel is null || rel.Length == 0)
            return false;

        if (ShouldIgnoreWatcherPath(rel))
            return false;

        var parentRel = GetParentRelative(rel);
        var parent = FindNode(parentRel);
        if (parent is null)
            return false;

        var name = Path.GetFileName(rel.Replace('/', Path.DirectorySeparatorChar));
        var idx = parent.Children.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;

        parent.Children.RemoveAt(idx);
        return true;
    }

    private bool ShouldIgnoreWatcherPath(string relativePath)
    {
        // Ignore events under a node_modules segment unless that node already has ChildrenLoaded.
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = Root;
        var built = "";
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            built = CombineRelative(built, seg);
            if (!IsNodeModulesName(seg))
            {
                var next = current.Children.Find(c =>
                    c.IsDirectory && c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
                if (next is not null)
                    current = next;
                continue;
            }

            var nm = current.Children.Find(c =>
                c.IsDirectory && c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
            if (nm is null)
                return true; // unknown node_modules — treat as skipped
            if (!nm.ChildrenLoaded)
                return true; // not expanded — ignore interior events
            // Expanded: only apply to the shallow children of this node_modules (not deeper).
            // Events for direct children have one more segment; deeper paths are ignored.
            return i < segments.Length - 2;
        }

        return false;
    }

    private bool IsUnderNodeModulesSegment(string relativePath)
    {
        foreach (var seg in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsNodeModulesName(seg))
                return true;
        }

        return false;
    }

    private void ScheduleLoadedResync()
    {
        if (_resyncScheduled || _disposed)
            return;

        _resyncScheduled = true;
        _ = Task.Run(() =>
        {
            try
            {
                List<DysonFileTreeNode> loaded;
                lock (_treeGate)
                    loaded = CollectLoadedDirectories(Root);

                foreach (var node in loaded)
                {
                    if (_disposed)
                        return;

                    var path = node.RelativePath.Length == 0 ? "." : node.RelativePath;
                    var entries = _fs.EnumerateEntries(path);
                    if (entries.IsError)
                        continue;

                    var onDisk = entries.Value
                        .Select(e => e.Name)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    lock (_treeGate)
                    {
                        // Remove missing
                        for (var i = node.Children.Count - 1; i >= 0; i--)
                        {
                            if (!onDisk.Contains(node.Children[i].Name))
                                node.Children.RemoveAt(i);
                        }

                        // Add new (shallow)
                        var known = node.Children.Select(c => c.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in entries.Value)
                        {
                            if (string.IsNullOrEmpty(entry.Name) || known.Contains(entry.Name))
                                continue;

                            InsertSorted(node.Children, new DysonFileTreeNode
                            {
                                Name = entry.Name,
                                RelativePath = CombineRelative(node.RelativePath, entry.Name),
                                IsDirectory = entry.IsDirectory,
                            });
                        }
                    }
                }

                Dirty = false;
                _notify();
            }
            finally
            {
                _resyncScheduled = false;
            }
        });
    }

    private static List<DysonFileTreeNode> CollectLoadedDirectories(DysonFileTreeNode node)
    {
        var list = new List<DysonFileTreeNode>();
        if (node.IsDirectory && node.ChildrenLoaded)
            list.Add(node);

        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
                list.AddRange(CollectLoadedDirectories(child));
        }

        return list;
    }

    internal DysonFileTreeNode? TryFindNode(string relativePath) => FindNode(relativePath);

    private DysonFileTreeNode? FindNode(string relativePath)
    {
        if (relativePath.Length == 0)
            return Root;

        var current = Root;
        foreach (var seg in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Children.Find(c =>
                c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            current = next;
        }

        return current;
    }

    private string? TryGetRelative(string fullPath)
    {
        var rel = _fs.GetRelativePath(fullPath);
        return rel.IsError ? null : rel.Value;
    }

    internal static bool IsNodeModulesName(string name) =>
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

    internal static string CombineRelative(string parent, string name) =>
        parent.Length == 0 ? name.Replace('\\', '/') : $"{parent}/{name.Replace('\\', '/')}";

    internal static string GetParentRelative(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        return idx < 0 ? "" : relativePath[..idx];
    }

    internal static void InsertSorted(List<DysonFileTreeNode> children, DysonFileTreeNode node)
    {
        var i = 0;
        while (i < children.Count && CompareNodes(children[i], node) < 0)
            i++;
        children.Insert(i, node);
    }

    internal static int CompareNodes(DysonFileTreeNode a, DysonFileTreeNode b)
    {
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private enum FsOpKind
    {
        Created,
        Deleted,
        Renamed,
    }

    private readonly record struct PendingFsOp(FsOpKind Kind, string FullPath, string? OldFullPath);
}

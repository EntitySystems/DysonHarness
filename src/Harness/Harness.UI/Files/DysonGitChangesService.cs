using DysonHarness;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.UI.Files;

/// <summary>
/// Process-lifetime git change list: per-workdir snapshot, root-most repo discovery,
/// and live refresh via <see cref="DysonGitRepoChangedEvent"/>.
/// </summary>
public sealed class DysonGitChangesService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DysonFileTreeService _fileTree;
    private readonly DysonGitRepoChangePublisher _publisher;
    private readonly Dictionary<DysonWorkspaceRootKey, DysonGitChangesState> _cache = new();
    private readonly object _gate = new();
    private readonly IDisposable _subscription;
    private CancellationTokenSource? _refreshCts;
    private bool _disposed;

    public DysonGitChangesService(
        IServiceScopeFactory scopeFactory,
        DysonFileTreeService fileTree,
        DysonMessageBus bus,
        DysonGitRepoChangePublisher publisher)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _fileTree = fileTree ?? throw new ArgumentNullException(nameof(fileTree));
        ArgumentNullException.ThrowIfNull(bus);
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

        var subscribed = bus.Subscribe<DysonGitRepoChangedEvent>(DysonBusScopes.Wildcard, OnRepoChanged);
        if (subscribed.IsError)
            throw new InvalidOperationException(subscribed.Error);

        _subscription = subscribed.Value;
    }

    public DysonGitChangesState? Active { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Switch the active workdir snapshot. Cached entries are reused until process exit;
    /// status is refreshed on activate and after git-repo bus events.
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
            _publisher.Unwatch();
            Notify();
            return VoidResult<string>.Success;
        }

        var id = workDirectoryId.Value;
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

        var activated = await SetActiveAsync(id, absolutePath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(subjectId))
        {
            lock (_gate)
            {
                if (Active is { } state)
                    state.SubjectId = subjectId;
            }
        }

        return activated;
    }

    /// <summary>Activate a specific workspace root (registered checkout or session worktree).</summary>
    public async Task<VoidResult<string>> SetActiveAsync(
        Guid workDirectoryId,
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var key = DysonWorkspaceRootKey.From(workDirectoryId, absolutePath);
        DysonGitChangesState state;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out state!))
            {
                state = new DysonGitChangesState(workDirectoryId);
                _cache[key] = state;
            }

            state.WorkAbsolutePath = Path.GetFullPath(absolutePath.Trim());
            Active = state;
        }

        Notify();
        await RefreshAsync(state, cancellationToken).ConfigureAwait(false);
        return VoidResult<string>.Success;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _subscription.Dispose();
        _publisher.Unwatch();
        lock (_gate)
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
            _cache.Clear();
            Active = null;
        }
    }

    private void OnRepoChanged(DysonGitRepoChangedEvent evt)
    {
        if (_disposed)
            return;

        lock (_gate)
        {
            var active = Active;
            if (active is null || evt.WorkDirectoryId != active.WorkDirectoryId)
                return;
        }

        _ = RefreshActiveAsync();
    }

    private async Task RefreshActiveAsync()
    {
        DysonGitChangesState? state;
        lock (_gate)
            state = Active;

        if (state is null || _disposed)
            return;

        try
        {
            await RefreshAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort background refresh; UI keeps last snapshot.
        }
    }

    private async Task RefreshAsync(DysonGitChangesState state, CancellationToken cancellationToken)
    {
        CancellationTokenSource myCts;
        lock (_gate)
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            myCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _refreshCts = myCts;
        }

        var ct = myCts.Token;
        state.IsLoading = true;
        Notify();

        try
        {
            if (string.IsNullOrWhiteSpace(state.WorkAbsolutePath))
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                DysonCloudSubjectScope.TryBind(scope.ServiceProvider, state.SubjectId);
                var store = scope.ServiceProvider.GetRequiredService<IDysonWorkDirectoryRepository>();
                var get = await store.GetAsync(state.WorkDirectoryId, ct)
                    .ConfigureAwait(false);
                if (get.IsError)
                {
                    state.Error = get.Error;
                    state.RepoRoot = null;
                    state.Entries = [];
                    return;
                }

                state.WorkAbsolutePath = get.Value.AbsolutePath;
            }

            // Prefer the active file-tree FS native root when it matches this workdir + path.
            var nativeRoot = state.WorkAbsolutePath;
            var tree = _fileTree.Active;
            var treeMatches = tree is not null
                && tree.WorkDirectoryId == state.WorkDirectoryId
                && DysonWorkspaceRootKey.SamePath(tree.AbsolutePath, state.WorkAbsolutePath)
                && !string.IsNullOrWhiteSpace(tree.FileSystem.NativeRootPath);
            if (treeMatches)
            {
                nativeRoot = tree!.FileSystem.NativeRootPath;
                state.WorkAbsolutePath = nativeRoot;
            }

            var root = await Task.Run(
                    () => treeMatches
                        ? DysonGitInfo.TryFindRootMostRepo(tree!.FileSystem)
                        : DysonGitInfo.TryFindRootMostRepo(nativeRoot),
                    ct)
                .ConfigureAwait(false);

            if (root.IsError)
            {
                state.Error = null;
                state.RepoRoot = null;
                state.Entries = [];
                state.NoRepo = true;
                _publisher.Unwatch();
                return;
            }

            state.NoRepo = false;
            state.RepoRoot = root.Value;
            // Watch as soon as a repo exists so porcelain failures still live-refresh.
            _ = _publisher.Watch(state.WorkDirectoryId, state.RepoRoot);

            var status = await Task.Run(
                    () => DysonGitInfo.TryGetStatusPorcelain(root.Value),
                    ct)
                .ConfigureAwait(false);

            if (status.IsError)
            {
                state.Error = status.Error;
                state.Entries = [];
                return;
            }

            state.Error = null;
            state.Entries = status.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer refresh — leave IsLoading for the winner.
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Entries = [];
        }
        finally
        {
            var stillCurrent = false;
            lock (_gate)
            {
                if (ReferenceEquals(_refreshCts, myCts))
                {
                    state.IsLoading = false;
                    stillCurrent = true;
                }
            }

            if (stillCurrent)
                Notify();
        }
    }

    private void Notify() => Changed?.Invoke();
}

/// <summary>Per-workdir git change snapshot.</summary>
public sealed class DysonGitChangesState
{
    public DysonGitChangesState(Guid workDirectoryId)
    {
        WorkDirectoryId = workDirectoryId;
    }

    public Guid WorkDirectoryId { get; }
    /// <summary>Cloud subject id for child-scope repository lookups; unused in Local mode.</summary>
    public string? SubjectId { get; set; }
    public string WorkAbsolutePath { get; set; } = "";
    public string? RepoRoot { get; set; }
    public bool NoRepo { get; set; }
    public bool IsLoading { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<DysonGitStatusEntry> Entries { get; set; } = [];

    public IEnumerable<DysonGitStatusEntry> OfKind(DysonGitChangeKind kind) =>
        Entries.Where(e => e.Kind == kind)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);
}

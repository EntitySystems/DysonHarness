namespace DysonHarness;

/// <summary>The four values a <see cref="DysonVersionControlService"/> is bound to for life.</summary>
public sealed record DysonVersionControlBinding(
    Guid WorkDirectoryId, string SubjectId, string AbsolutePath, string? Origin);

/// <summary>Test seams for the per-work-directory loop. Production uses the real delay, clock and git origin.</summary>
internal sealed record DysonVersionControlSeams(
    Func<TimeSpan, CancellationToken, Task> Delay,
    Func<DateTimeOffset> Now,
    Func<string, CancellationToken, Task<Result<string, string>>> GetOrigin)
{
    public static DysonVersionControlSeams Default { get; } = new(
        static (span, ct) => Task.Delay(span, ct),
        static () => DateTimeOffset.UtcNow,
        DysonGitInfo.TryGetOriginAsync);
}

/// <summary>
/// All source-control state for one work directory: detected provider, ETags, snapshot, leases and the poll loop.
/// Public members only take a lock and signal; every provider call runs inside the <see cref="Task.Run"/> loop.
/// </summary>
public sealed class DysonVersionControlService : IDisposable
{
    /// <summary>Quiet window after the last shell command before one ETag poll.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    /// <summary>At most this many job fetches per tick, round-robin. ponytail: ceiling = 3 sequential gh calls per tick; upgrade path = GraphQL batching.</summary>
    internal const int MaxJobFetchesPerTick = 3;

    private readonly DysonVersionControlBinding _binding;
    private readonly IReadOnlyList<IVersionControlProvider> _providers;
    private readonly DysonMessageBus _bus;
    private readonly DysonVersionControlSeams _seams;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _lock = new();
    private readonly IDisposable? _shellSubscription;

    private DysonPipelineSnapshot? _snapshot;
    private IVersionControlProvider? _provider;
    private DysonPipelineTarget? _target;
    private bool _detected;
    private string? _runsEtag;
    private readonly Dictionary<long, string?> _jobsEtag = new();
    private readonly Dictionary<long, (int Attempt, DateTimeOffset UpdatedAt, IReadOnlyList<DysonPipelineJob> Jobs)> _jobsCache = new();
    private readonly LinkedList<long> _wanted = new();
    private readonly HashSet<long> _wantedSet = new();
    private int _jobCursor;
    private int _pillLeases;
    private int _panelLeases;
    private int _panelOpened;
    private int _consecutiveFailures;
    private int? _rateRemaining;
    private DateTimeOffset? _retryAfter;
    private bool _clearBackoff;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _waitCts;
    private Task? _loop;
    private bool _busy;
    private bool _pollAgain;
    private bool _disposed;
    private CancellationTokenSource? _debounceCts;
    private int _debounceGeneration;

    internal DysonVersionControlService(
        DysonVersionControlBinding binding,
        IReadOnlyList<IVersionControlProvider> providers,
        DysonMessageBus bus,
        DysonVersionControlSeams seams)
    {
        _binding = binding;
        _providers = providers;
        _bus = bus;
        _seams = seams;

        var sub = bus.Subscribe<DysonShellVcsCommandRanEvent>(
            DysonBusScopes.WorkDirectory(binding.WorkDirectoryId), OnShellCommand);
        _shellSubscription = sub.IsSuccess ? sub.Value : null;
    }

    public Guid WorkDirectoryId => _binding.WorkDirectoryId;

    public string SubjectId => _binding.SubjectId;

    /// <summary>Lock-free read of the latest immutable snapshot.</summary>
    public DysonPipelineSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// First lease starts the loop; disposing the last lease cancels it.
    /// <paramref name="panelOpen"/> is a panel lease (job fetches, idle 15 s); false is a pill lease.
    /// </summary>
    public IDisposable Watch(bool panelOpen)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (panelOpen)
            {
                _panelLeases++;
                _panelOpened++;
            }
            else
                _pillLeases++;
            if (_loop is not { IsCompleted: false })
                ForgetNotDetectedLocked();
            EnsureLoopLocked();
            if (panelOpen)
                CancelWaitLocked();
            return new Lease(this, panelOpen);
        }
    }

    /// <summary>User expanded a run. Remembered until the jobs arrive; wakes the loop.</summary>
    public void RequestJobs(long runId)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            if (_wantedSet.Add(runId))
                _wanted.AddLast(runId);
            WakeLocked();
        }
    }

    /// <summary>Refresh or Retry. Clears a CliMissing / auth backoff and re-probes gh.</summary>
    public void RefreshNow()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _clearBackoff = true;
            (_provider as DysonGitHubVersionControlProvider)?.ResetProbe();
            ForgetNotDetectedLocked();
            WakeLocked();
        }
    }

    /// <summary>
    /// Shell hook. Restarts a 5 s debounce; the delayed continuation wakes the loop or runs one one-shot tick.
    /// Lock and schedule only — no I/O, so it is safe on the ShellExecute publisher thread.
    /// ponytail: no max-wait, so a long burst only postpones the poll; upgrade path = a 30 s max-wait.
    /// </summary>
    public void RequestPollSoon()
    {
        CancellationTokenSource cts;
        int generation;
        lock (_lock)
        {
            if (_disposed)
                return;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            cts = new CancellationTokenSource();
            _debounceCts = cts;
            generation = ++_debounceGeneration;
        }

        _ = DebounceAsync(cts, generation);
    }

    public void Dispose()
    {
        CancellationTokenSource? loopCts;
        CancellationTokenSource? debounceCts;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounceGeneration++;
            loopCts = _loopCts;
            _loopCts = null;
            debounceCts = _debounceCts;
            _debounceCts = null;
        }

        _shellSubscription?.Dispose();
        try { debounceCts?.Cancel(); } catch (ObjectDisposedException) { }
        debounceCts?.Dispose();
        try { loopCts?.Cancel(); } catch (ObjectDisposedException) { }
        loopCts?.Dispose();
    }

    /// <summary>Next poll delay. See the plan's 3.2 table; the only case above 120 s is a rate-limit reset.</summary>
    internal static TimeSpan NextInterval(
        bool panelOpen, bool anyActive, DysonPipelineError? error, int consecutiveFailures,
        int? rateRemaining, DateTimeOffset? retryAfter, TimeSpan? pollHint, DateTimeOffset now)
    {
        var ceiling = TimeSpan.FromSeconds(120);

        if (error?.Kind == DysonPipelineErrorKind.RateLimited)
        {
            var wait = retryAfter is { } at && at > now ? at - now : TimeSpan.FromSeconds(60);
            return wait < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
        }

        var activeBase = TimeSpan.FromSeconds(15);
        TimeSpan baseInterval;
        if (anyActive || panelOpen)
            baseInterval = activeBase;
        else if (error?.Kind is DysonPipelineErrorKind.CliMissing
            or DysonPipelineErrorKind.NotAuthenticated
            or DysonPipelineErrorKind.RepoNotFound)
            baseInterval = ceiling;
        else if (error?.Kind == DysonPipelineErrorKind.Failed)
        {
            var shift = Math.Clamp(consecutiveFailures, 0, 10);
            var scaled = TimeSpan.FromTicks(activeBase.Ticks << shift);
            baseInterval = scaled > ceiling ? ceiling : scaled;
        }
        else
            baseInterval = ceiling;

        if (rateRemaining is < 500)
        {
            var scaled = TimeSpan.FromTicks(baseInterval.Ticks * 3);
            baseInterval = scaled > ceiling ? ceiling : scaled;
        }

        if (pollHint is { } hint && hint > baseInterval)
            baseInterval = hint > ceiling ? ceiling : hint;

        return baseInterval;
    }

    private void OnShellCommand(DysonShellVcsCommandRanEvent evt)
    {
        if (evt.WorkDirectoryId != _binding.WorkDirectoryId)
            return;
        RequestPollSoon();
    }

    private async Task DebounceAsync(CancellationTokenSource cts, int generation)
    {
        try
        {
            await _seams.Delay(Debounce, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed || generation != _debounceGeneration)
                return;
            // `git remote add` may have just given this work directory a GitHub remote.
            ForgetNotDetectedLocked();
            // A loop parked on its idle wait is still a loop: wake it instead of starting a second tick.
            if (_loopCts is not null)
            {
                _pollAgain = true;
                CancelWaitLocked();
                return;
            }

            WakeLocked();
        }
    }

    private void EnsureLoopLocked()
    {
        if (_loop is { IsCompleted: false })
            return;
        _loopCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loopCts = cts;
        _loop = Task.Run(() => LoopAsync(cts));
    }

    /// <summary>
    /// With a loop: cancel its wait so the next tick runs at once. With no loop: one one-shot tick, then stop.
    /// A tick already running only sets <see cref="_pollAgain"/>, so provider calls never overlap.
    /// </summary>
    private void WakeLocked()
    {
        if (_loopCts is not null)
        {
            _pollAgain = true;
            CancelWaitLocked();
            return;
        }

        if (LeaseCountLocked() > 0 && !_disposed)
        {
            // Leased but the loop ended (NotDetected): restart it instead of a lone one-shot.
            EnsureLoopLocked();
            return;
        }

        if (_busy)
        {
            _pollAgain = true;
            return;
        }

        _busy = true;
        _pollAgain = false;
        _ = Task.Run(() => RunOneShotAsync());
    }

    /// <summary>A NotDetected result is not final: the next Watch, RefreshNow or shell poll re-resolves the origin.</summary>
    private void ForgetNotDetectedLocked()
    {
        if (_detected && _provider is null)
            _detected = false;
    }

    private void CancelWaitLocked()
    {
        if (_waitCts is not { } cts)
            return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RunOneShotAsync()
    {
        await _turn.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await TickAsync(panelOpen: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A one-shot has no token of its own; only the loop root is expected to see this.
        }
        finally
        {
            _turn.Release();
            bool again;
            lock (_lock)
            {
                _busy = false;
                again = _pollAgain && !_disposed && _loopCts is null && LeaseCountLocked() == 0;
                if (again)
                {
                    _pollAgain = false;
                    _busy = true;
                }
            }

            if (again)
                _ = Task.Run(() => RunOneShotAsync());
        }
    }

    private async Task LoopAsync(CancellationTokenSource loopCts)
    {
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(loopCts.Token);
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                await _turn.WaitAsync(stopped.Token).ConfigureAwait(false);
                bool panel;
                lock (_lock)
                {
                    if (LeaseCountLocked() == 0 || _disposed)
                    {
                        _turn.Release();
                        return;
                    }

                    panel = _panelLeases > 0;
                    if (_panelOpened > 0)
                    {
                        panel = true;
                        _panelOpened--;
                    }

                    _pollAgain = false;
                    _busy = true;
                }

                try
                {
                    await TickAsync(panel, stopped.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock)
                        _busy = false;
                    _turn.Release();
                }

                TimeSpan wait;
                CancellationTokenSource waitCts;
                lock (_lock)
                {
                    if (LeaseCountLocked() == 0 || _disposed || stopped.IsCancellationRequested)
                        return;
                    if (_pollAgain || _panelOpened > 0)
                        continue;
                    wait = ComputeWaitLocked();
                    waitCts = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                    _waitCts = waitCts;
                }

                try
                {
                    await _seams.Delay(wait, waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
                {
                    // Woken by RefreshNow, RequestJobs or the shell debounce.
                }
                finally
                {
                    lock (_lock)
                    {
                        if (ReferenceEquals(_waitCts, waitCts))
                            _waitCts = null;
                    }

                    waitCts.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Loop root: disposal or the last lease cancelled the token.
        }
        finally
        {
            lock (_lock)
            {
                var restart = false;
                if (ReferenceEquals(_loopCts, loopCts))
                {
                    _loopCts = null;
                    _loop = null;
                    loopCts.Dispose();
                    // A wake that landed while this loop was stopping (e.g. Retry right after NotDetected).
                    restart = _pollAgain && !_disposed && LeaseCountLocked() > 0;
                }

                _busy = false;
                CancelWaitLocked();
                if (restart)
                {
                    ForgetNotDetectedLocked();
                    EnsureLoopLocked();
                }
            }
        }
    }

    private async Task TickAsync(bool panelOpen, CancellationToken token)
    {
        if (!await EnsureDetectedAsync(token).ConfigureAwait(false))
            return;

        var provider = _provider!;
        var target = _target!;
        bool clearBackoff;
        string? runsEtag;
        lock (_lock)
        {
            clearBackoff = _clearBackoff;
            _clearBackoff = false;
            runsEtag = _runsEtag;
        }

        if (clearBackoff)
            (provider as DysonGitHubVersionControlProvider)?.ResetProbe();

        var runsResult = await provider.ListRunsAsync(target, runsEtag, token).ConfigureAwait(false);

        IReadOnlyList<DysonPipelineRun> runs;
        DysonPipelineError? error = null;
        if (runsResult.IsError)
        {
            error = runsResult.Error;
            runs = Snapshot?.Runs ?? [];
        }
        else if (runsResult.Value.NotModified)
        {
            runs = Snapshot?.Runs ?? [];
        }
        else
        {
            runs = runsResult.Value.Items;
            lock (_lock)
                _runsEtag = runsResult.Value.ETag;
        }

        var jobs = await FetchJobsAsync(provider, target, runs, panelOpen, token).ConfigureAwait(false);

        DysonPipelineSnapshot next;
        bool publish;
        lock (_lock)
        {
            if (runsResult.IsSuccess)
            {
                _consecutiveFailures = 0;
                _rateRemaining = runsResult.Value.RateRemaining;
                _retryAfter = runsResult.Value.RateReset;
            }
            else
            {
                _consecutiveFailures++;
                if (error?.Kind == DysonPipelineErrorKind.RateLimited)
                    _retryAfter = error.RetryAfter;
            }

            var running = 0;
            var queued = 0;
            foreach (var run in runs)
            {
                if (run.State == DysonPipelineState.Running)
                    running++;
                else if (run.State is DysonPipelineState.Queued or DysonPipelineState.Waiting)
                    queued++;
            }

            next = new DysonPipelineSnapshot(
                _binding.WorkDirectoryId, provider.Id, provider.DisplayName, target,
                runs, jobs, running, queued, error, _seams.Now());

            var previous = _snapshot;
            publish = previous is null
                || !previous.Runs.SequenceEqual(next.Runs)
                || !JobsEqual(previous.Jobs, next.Jobs)
                || !Equals(previous.Error, next.Error);
            Volatile.Write(ref _snapshot, next);
        }

        if (publish)
            _bus.Publish(DysonBusScopes.WorkDirectory(_binding.WorkDirectoryId), new DysonPipelinesChangedEvent(_binding.WorkDirectoryId));
    }

    private async Task<bool> EnsureDetectedAsync(CancellationToken token)
    {
        lock (_lock)
        {
            if (_detected)
                return _provider is not null;
        }

        var origin = _binding.Origin;
        if (string.IsNullOrWhiteSpace(origin))
        {
            var fetched = await _seams.GetOrigin(_binding.AbsolutePath, token).ConfigureAwait(false);
            origin = fetched.IsSuccess ? fetched.Value : null;
        }

        var detected = DysonVersionControlDetector.Detect(_providers, origin);
        lock (_lock)
        {
            if (detected.IsError)
            {
                _detected = true;
                _provider = null;
                _target = null;
                Volatile.Write(ref _snapshot, new DysonPipelineSnapshot(
                    _binding.WorkDirectoryId, null, null, null, [], new Dictionary<long, IReadOnlyList<DysonPipelineJob>>(),
                    0, 0, detected.Error, _seams.Now()));
            }
            else
            {
                _detected = true;
                _provider = detected.Value.Provider;
                _target = detected.Value.Target;
            }
        }

        if (detected.IsError)
        {
            _bus.Publish(DysonBusScopes.WorkDirectory(_binding.WorkDirectoryId), new DysonPipelinesChangedEvent(_binding.WorkDirectoryId));
            StopLoop();
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyList<DysonPipelineJob>>> FetchJobsAsync(
        IVersionControlProvider provider, DysonPipelineTarget target, IReadOnlyList<DysonPipelineRun> runs,
        bool panelOpen, CancellationToken token)
    {
        if (!panelOpen)
            return CurrentJobs(runs);

        List<(DysonPipelineRun Run, string? Etag)> candidates;
        lock (_lock)
        {
            candidates = new();
            var seen = new HashSet<long>();
            foreach (var run in runs)
            {
                if (run.State is not (DysonPipelineState.Queued or DysonPipelineState.Waiting or DysonPipelineState.Running))
                    continue;
                if (!seen.Add(run.Id))
                    continue;
                if (Fresh(run))
                    continue;
                candidates.Add((run, EtagOf(run.Id)));
            }

            foreach (var id in _wanted)
            {
                var run = runs.FirstOrDefault(r => r.Id == id);
                if (run is null || !seen.Add(run.Id) || Fresh(run))
                    continue;
                candidates.Add((run, EtagOf(run.Id)));
            }
        }

        var fetched = 0;
        for (var n = 0; n < candidates.Count && fetched < MaxJobFetchesPerTick; n++)
        {
            var index = (_jobCursor + n) % candidates.Count;
            var (run, etag) = candidates[index];
            var result = await provider.ListJobsAsync(target, run.Id, etag, token).ConfigureAwait(false);
            fetched++;
            if (result.IsError)
                continue;
            lock (_lock)
            {
                if (!result.Value.NotModified)
                {
                    _jobsCache[run.Id] = (run.Attempt, run.UpdatedAt, result.Value.Items);
                    _jobsEtag[run.Id] = result.Value.ETag;
                }

                if (_wantedSet.Remove(run.Id))
                    _wanted.Remove(run.Id);
            }
        }

        if (candidates.Count > 0)
            _jobCursor = (_jobCursor + fetched) % candidates.Count;

        lock (_lock)
            PruneLocked(runs);

        return CurrentJobs(runs);
    }

    private bool Fresh(DysonPipelineRun run) =>
        _jobsCache.TryGetValue(run.Id, out var cached) && cached.Attempt == run.Attempt && cached.UpdatedAt == run.UpdatedAt;

    private string? EtagOf(long runId) => _jobsEtag.TryGetValue(runId, out var etag) ? etag : null;

    private void PruneLocked(IReadOnlyList<DysonPipelineRun> runs)
    {
        if (_jobsCache.Count == 0)
            return;
        var live = new HashSet<long>(runs.Select(r => r.Id));
        foreach (var id in _jobsCache.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _jobsCache.Remove(id);
            _jobsEtag.Remove(id);
        }
    }

    private IReadOnlyDictionary<long, IReadOnlyList<DysonPipelineJob>> CurrentJobs(IReadOnlyList<DysonPipelineRun> runs)
    {
        lock (_lock)
        {
            var jobs = new Dictionary<long, IReadOnlyList<DysonPipelineJob>>();
            foreach (var run in runs)
            {
                if (_jobsCache.TryGetValue(run.Id, out var cached))
                    jobs[run.Id] = cached.Jobs;
            }

            return jobs;
        }
    }

    private TimeSpan ComputeWaitLocked()
    {
        var snapshot = _snapshot;
        var anyActive = snapshot?.ActiveCount > 0;
        // ponytail: X-Poll-Interval is not surfaced by the provider yet, so the hint stays null;
        // upgrade path = read it off the fetch once a provider returns one.
        return NextInterval(
            _panelLeases > 0, anyActive, snapshot?.Error, _consecutiveFailures,
            _rateRemaining, _retryAfter, pollHint: null, _seams.Now());
    }

    private int LeaseCountLocked() => _pillLeases + _panelLeases;

    private void ReleaseLease(bool panelOpen)
    {
        lock (_lock)
        {
            if (panelOpen)
                _panelLeases = Math.Max(0, _panelLeases - 1);
            else
                _pillLeases = Math.Max(0, _pillLeases - 1);
            if (LeaseCountLocked() == 0)
                StopLoopLocked();
        }
    }

    private void StopLoop()
    {
        lock (_lock)
            StopLoopLocked();
    }

    private void StopLoopLocked()
    {
        CancelWaitLocked();
        if (_loopCts is not { } cts)
            return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static bool JobsEqual(
        IReadOnlyDictionary<long, IReadOnlyList<DysonPipelineJob>> left,
        IReadOnlyDictionary<long, IReadOnlyList<DysonPipelineJob>> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var (id, jobs) in left)
        {
            if (!right.TryGetValue(id, out var other) || !jobs.SequenceEqual(other))
                return false;
        }

        return true;
    }

    private sealed class Lease(DysonVersionControlService owner, bool panelOpen) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            owner.ReleaseLease(panelOpen);
        }
    }
}

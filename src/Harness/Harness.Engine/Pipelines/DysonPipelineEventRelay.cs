using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace DysonHarness;

/// <summary>One target of the relay: a live root Meta session and how to reach its work directory's pipeline service.</summary>
public sealed record DysonPipelineRelayTarget(
    Guid SessionId,
    Guid WorkDirectoryId,
    Func<CancellationToken, Task<Result<DysonVersionControlService, string>>> Resolve);

/// <summary>
/// Turns <see cref="DysonPipelineEvent"/>s into one coalesced text block per window for each live root Meta session.
/// Per work directory: one <see cref="DysonVersionControlService.WatchEvents"/> lease and one bus subscription that only
/// writes to per-session channels (publish is synchronous on the poll thread). Per session: a reader task coalesces
/// (window, newest state wins, per-run cap, text cap, minimum gap) and calls <c>deliver</c> off the publisher thread.
/// Delivery is never suppressed here; chatter control lives in <see cref="Instruction"/>. The relay never interrupts:
/// the deliver callback only injects a queued comment or enqueues a prompt (see DysonSessionRuntime.PipelineEvents).
/// Cloud mode registers no provider factory, so <c>Resolve</c> fails and no lease or events ever exist.
/// </summary>
public sealed class DysonPipelineEventRelay : IDisposable
{
    /// <summary>Quiet window after the first pending event before a batch is delivered.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>Minimum time between two non-urgent deliveries to one session.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(20);

    /// <summary>A work directory whose service could not be resolved is retried no sooner than this.</summary>
    internal static readonly TimeSpan ResolveRetry = TimeSpan.FromSeconds(30);

    internal const int MaxLinesPerRun = 8; // ponytail: per-run cap is a count, not a size
    internal const int MaxLinesChars = 1500;

    /// <summary>
    /// Rides with every batch (the Meta system prompt is untouched). The transcript marker
    /// <see cref="DysonInjectedUserComments.PipelineMarker"/> is added by the delivery path, not repeated here.
    /// </summary>
    public const string Instruction = """
        This is an automatic CI/CD status update, not a message from the user.
        Read it so you know the current pipeline state and can answer questions about it accurately.
        Post NO user-visible message for simple state updates (run queued or started, a job started or passed, a run succeeded) unless the update is important or relevant to the current conversation. For a routine update the correct response is silence: no message to the user and no tool calls (do not call ListPipelineRuns, do not start drones, reruns or fixes) beyond what the user asked for.
        Share briefly only when it matters: a job or run FAILED, was cancelled or timed out; the user is waiting on this run; or the user asked about it. Then write at most one short message, preferably a PipelineRun A2UI component (runId, or jobIds for only the failed jobs), reusing the same surfaceId as earlier PipelineRun surfaces in this conversation so it updates in place instead of adding a new one.
        When new jobs start for a run the user is watching, update that PipelineRun surface rather than posting text.
        Never repeat an update the user already saw. Take no action on your own unless the user asked you to.
        """;

    private readonly DysonMessageBus _bus;
    private readonly Func<Guid, string, CancellationToken, Task<VoidResult<string>>> _deliver;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _window;
    private readonly TimeSpan _minGap;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _disposed;

    public DysonPipelineEventRelay(
        DysonMessageBus bus, Func<Guid, string, CancellationToken, Task<VoidResult<string>>> deliver)
        : this(bus, deliver, static (span, ct) => Task.Delay(span, ct), static () => DateTimeOffset.UtcNow, Window, MinGap)
    {
    }

    internal DysonPipelineEventRelay(
        DysonMessageBus bus,
        Func<Guid, string, CancellationToken, Task<VoidResult<string>>> deliver,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTimeOffset> now,
        TimeSpan window,
        TimeSpan minGap)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        _delay = delay;
        _now = now;
        _window = window;
        _minGap = minGap;
    }

    /// <summary>Work directories currently holding an events lease. Tests only.</summary>
    internal int LeasedWorkDirectoryCount
    {
        get
        {
            lock (_lock)
                return _entries.Values.Count(e => e.Lease is not null);
        }
    }

    /// <summary>
    /// Makes the relay match <paramref name="targets"/> (the live root Meta sessions): a worker per session, a lease and
    /// subscription per work directory, everything else torn down. Serialized; cheap when nothing changed.
    /// A work directory whose service cannot be resolved (cloud, no Actions provider) is retried at most every
    /// <see cref="ResolveRetry"/> and only when a later sync runs. ponytail: no timer of its own; upgrade path = back-off timer.
    /// </summary>
    public async Task SyncAsync(IReadOnlyList<DysonPipelineRelayTarget> targets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var needLease = new List<(Entry Entry, DysonPipelineRelayTarget Target)>();
            lock (_lock)
            {
                if (_disposed)
                    return;
                Reconcile(targets, needLease);
            }

            foreach (var (entry, target) in needLease)
            {
                IDisposable? lease = null;
                Result<DysonVersionControlService, string> resolved;
                try
                {
                    resolved = await target.Resolve(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Boundary: the resolver belongs to a host that may be disposed.
                    resolved = Result<DysonVersionControlService, string>.AsError(ex.Message);
                }

                if (resolved.IsSuccess)
                {
                    try
                    {
                        lease = resolved.Value.WatchEvents();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Boundary: the registry disposed the service (work directory deleted) between resolve and lease.
                    }
                }

                lock (_lock)
                {
                    entry.Resolving = false;
                    if (lease is null)
                        entry.FailedAt = _now();
                    else if (_disposed || !_entries.TryGetValue(entry.WorkDirectoryId, out var current) || !ReferenceEquals(current, entry))
                        lease.Dispose();
                    else
                        entry.Lease = lease;
                }
            }
        }
        finally
        {
            // A cancelled resolve leaves the remaining entries marked Resolving: clear them so a later sync retries.
            lock (_lock)
            {
                foreach (var entry in _entries.Values)
                    entry.Resolving = false;
            }

            _gate.Release();
        }
    }

    private void Reconcile(IReadOnlyList<DysonPipelineRelayTarget> targets, List<(Entry, DysonPipelineRelayTarget)> needLease)
    {
        var wanted = targets.GroupBy(t => t.WorkDirectoryId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var entry in _entries.Values.ToArray())
        {
            if (!wanted.TryGetValue(entry.WorkDirectoryId, out var group))
            {
                _entries.Remove(entry.WorkDirectoryId);
                entry.Dispose();
                continue;
            }

            foreach (var id in entry.Workers.Keys.ToArray())
            {
                if (group.Any(t => t.SessionId == id))
                    continue;
                if (entry.Workers.TryRemove(id, out var gone))
                    gone.Stop();
            }
        }

        foreach (var (workDirectoryId, group) in wanted)
        {
            if (!_entries.TryGetValue(workDirectoryId, out var entry))
            {
                entry = new Entry(workDirectoryId);
                var sub = _bus.Subscribe<DysonPipelineEvent>(
                    DysonBusScopes.WorkDirectory(workDirectoryId), evt => entry.Offer(evt));
                entry.Subscription = sub.IsSuccess ? sub.Value : null;
                _entries[workDirectoryId] = entry;
            }

            foreach (var target in group)
            {
                if (entry.Workers.ContainsKey(target.SessionId))
                    continue;
                var worker = new Worker();
                entry.Workers[target.SessionId] = worker;
                var sessionId = target.SessionId;
                _ = Task.Run(() => RunWorkerAsync(sessionId, worker));
            }

            if (entry.Lease is null && !entry.Resolving && (entry.FailedAt is not { } failedAt || _now() - failedAt >= ResolveRetry))
            {
                entry.Resolving = true;
                needLease.Add((entry, group[0]));
            }
        }
    }

    private async Task RunWorkerAsync(Guid sessionId, Worker worker)
    {
        var ct = worker.Token;
        var reader = worker.Reader;
        DateTimeOffset? lastSent = null;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var batch = new DysonPipelineEventBatch();
                Drain(reader, batch);
                if (batch.Count == 0)
                    continue;

                if (!batch.HasImportant)
                    await CollectWindowAsync(reader, batch, ct).ConfigureAwait(false);

                // ponytail: an urgent event arriving during this gap waits out the gap; upgrade path = race it like the window.
                if (!batch.HasImportant && lastSent is { } last && _minGap - (_now() - last) is { } wait && wait > TimeSpan.Zero)
                {
                    await _delay(wait, ct).ConfigureAwait(false);
                    Drain(reader, batch);
                }

                lastSent = _now();
                // A failed delivery drops this batch: the next event batch carries the current state anyway.
                try
                {
                    await _deliver(sessionId, batch.Format(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Boundary: a throwing deliver callback must not kill this session's worker (its channel would grow unread).
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session left the relay or the relay was disposed.
        }
    }

    private async Task CollectWindowAsync(ChannelReader<DysonPipelineEvent> reader, DysonPipelineEventBatch batch, CancellationToken ct)
    {
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var windowTask = _delay(_window, windowCts.Token);
        try
        {
            while (!batch.HasImportant)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readTask = reader.WaitToReadAsync(readCts.Token).AsTask();
                var done = await Task.WhenAny(windowTask, readTask).ConfigureAwait(false);
                if (done == windowTask)
                {
                    readCts.Cancel();
                    try { await readTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    return;
                }

                if (!await readTask.ConfigureAwait(false))
                    return;
                Drain(reader, batch);
            }
        }
        finally
        {
            windowCts.Cancel();
        }
    }

    private static void Drain(ChannelReader<DysonPipelineEvent> reader, DysonPipelineEventBatch batch)
    {
        while (reader.TryRead(out var evt))
            batch.Add(evt);
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }

        foreach (var entry in entries)
            entry.Dispose();
    }

    private sealed class Entry(Guid workDirectoryId) : IDisposable
    {
        public Guid WorkDirectoryId { get; } = workDirectoryId;
        public ConcurrentDictionary<Guid, Worker> Workers { get; } = new();
        public IDisposable? Subscription { get; set; }
        public IDisposable? Lease { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public bool Resolving { get; set; }

        /// <summary>Bus handler: channel writes only.</summary>
        public void Offer(DysonPipelineEvent evt)
        {
            if (evt.WorkDirectoryId != WorkDirectoryId)
                return;
            foreach (var worker in Workers.Values)
                worker.Offer(evt);
        }

        public void Dispose()
        {
            Subscription?.Dispose();
            Lease?.Dispose();
            foreach (var worker in Workers.Values)
                worker.Stop();
            Workers.Clear();
        }
    }

    private sealed class Worker
    {
        private readonly Channel<DysonPipelineEvent> _channel = Channel.CreateUnbounded<DysonPipelineEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly CancellationTokenSource _cts = new();

        public ChannelReader<DysonPipelineEvent> Reader => _channel.Reader;
        public CancellationToken Token => _cts.Token;

        public void Offer(DysonPipelineEvent evt) => _channel.Writer.TryWrite(evt);

        public void Stop()
        {
            _channel.Writer.TryComplete();
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}

/// <summary>
/// One session's pending events: newest state per run / per job wins (a job Started then Completed in one window is one
/// Completed line), at most <see cref="DysonPipelineEventRelay.MaxLinesPerRun"/> lines per run, lines capped to
/// <see cref="DysonPipelineEventRelay.MaxLinesChars"/> characters. Not thread-safe: one reader task owns it.
/// </summary>
internal sealed class DysonPipelineEventBatch
{
    private readonly List<DysonPipelineEvent> _events = [];
    private readonly Dictionary<(long Run, long Job), int> _index = new();

    public int Count => _events.Count;

    public bool HasImportant => _events.Any(e => e.IsImportant);

    public void Add(DysonPipelineEvent evt)
    {
        var key = (evt.RunId, evt.JobId ?? 0L);
        if (_index.TryGetValue(key, out var at))
        {
            // A failure stays in the batch even when a re-run of the same run id starts in the same window.
            if (!_events[at].IsImportant || evt.IsImportant)
                _events[at] = evt;
        }
        else
        {
            _index[key] = _events.Count;
            _events.Add(evt);
        }
    }

    /// <summary>The instruction text followed by the batch lines.</summary>
    public string Format()
    {
        var lines = new List<string>();
        foreach (var run in _events.GroupBy(e => e.RunId))
        {
            var ordered = run.OrderBy(e => e.JobId is null ? 0 : e.IsImportant ? 1 : 2).ToList();
            foreach (var evt in ordered.Take(DysonPipelineEventRelay.MaxLinesPerRun))
                lines.Add(Line(evt));
            if (ordered.Count > DysonPipelineEventRelay.MaxLinesPerRun)
                lines.Add($"- +{ordered.Count - DysonPipelineEventRelay.MaxLinesPerRun} more updates for run {run.Key}");
        }

        var sb = new StringBuilder(DysonPipelineEventRelay.Instruction);
        sb.Append("\n\nUpdates:");
        var used = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (used + lines[i].Length > DysonPipelineEventRelay.MaxLinesChars)
            {
                sb.Append("\n- +").Append(lines.Count - i).Append(" more updates not shown");
                break;
            }

            used += lines[i].Length;
            sb.Append('\n').Append(lines[i]);
        }

        return sb.ToString();
    }

    private static string Line(DysonPipelineEvent e)
    {
        var workflow = $"\"{Clean(e.Workflow)}\"";
        var elapsed = e.Elapsed is { } span && e.Kind is DysonPipelineEventKind.RunCompleted or DysonPipelineEventKind.JobCompleted
            ? $" after {DysonPipelineText.FormatDuration(span)}"
            : "";
        if (e.JobId is null)
        {
            var what = e.Kind switch
            {
                DysonPipelineEventKind.RunCreated => $"created ({Clean(e.StateLabel)})",
                DysonPipelineEventKind.RunStarted => "started",
                _ => $"completed: {Clean(e.StateLabel)}",
            };
            return $"- run {e.RunId} {workflow} ({Clean(e.Branch)}@{DysonPipelineText.ShortSha(e.HeadSha)}): {what}{elapsed} - {e.HtmlUrl}";
        }

        var jobWhat = e.Kind == DysonPipelineEventKind.JobStarted ? "started" : $"completed: {Clean(e.StateLabel)}";
        return $"- job {e.JobId} \"{Clean(e.JobName ?? "")}\" (run {e.RunId} {workflow}): {jobWhat}{elapsed} - {e.HtmlUrl}";
    }

    /// <summary>Names come from the repository: one line, bounded.</summary>
    private static string Clean(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Replace('"', '\'');
        return flat.Length <= 80 ? flat : flat[..80];
    }
}

using System.Collections.Concurrent;

namespace DysonHarness.Tests;

public sealed class DysonVersionControlServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Counts_running_queued_and_waiting()
    {
        var provider = new FakeProvider
        {
            Runs =
            [
                Run(1, DysonPipelineState.Running), Run(2, DysonPipelineState.Running),
                Run(3, DysonPipelineState.Queued), Run(4, DysonPipelineState.Waiting),
                Run(5, DysonPipelineState.Succeeded),
            ],
        };
        await using var h = Harness.Create(provider);
        var published = h.Listen();
        using var lease = h.Service.Watch(panelOpen: false);

        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = h.Service.Snapshot!;
        Assert.Equal(2, snapshot.RunningCount);
        Assert.Equal(2, snapshot.QueuedCount);
        Assert.Equal(4, snapshot.ActiveCount);
    }

    [Theory]
    [MemberData(nameof(IntervalRows))]
    public void NextInterval_matches_the_table(
        bool panel, bool active, DysonPipelineErrorKind? kind, int failures, int? remaining,
        int? retryAfterSeconds, int? hintSeconds, int expectedSeconds)
    {
        var now = T0;
        var error = kind is { } k ? new DysonPipelineError(k, "x", retryAfterSeconds is { } s ? now.AddSeconds(s) : null) : null;
        var wait = DysonVersionControlService.NextInterval(
            panel, active, error, failures, remaining,
            error?.RetryAfter, hintSeconds is { } h ? TimeSpan.FromSeconds(h) : null, now);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), wait);
    }

    public static TheoryData<bool, bool, DysonPipelineErrorKind?, int, int?, int?, int?, int> IntervalRows() => new()
    {
        { false, true, null, 0, null, null, null, 15 },          // anything active, pill
        { true, true, null, 0, null, null, null, 15 },           // anything active, panel
        { true, false, null, 0, null, null, null, 15 },          // panel open, idle
        { false, false, null, 0, null, null, null, 120 },        // pill only, idle
        { false, false, DysonPipelineErrorKind.CliMissing, 0, null, null, null, 120 },
        { false, false, DysonPipelineErrorKind.NotAuthenticated, 0, null, null, null, 120 },
        { false, false, DysonPipelineErrorKind.RepoNotFound, 0, null, null, null, 120 },
        { false, false, DysonPipelineErrorKind.Failed, 0, null, null, null, 15 },   // 15 << 0
        { false, false, DysonPipelineErrorKind.Failed, 2, null, null, null, 60 },   // 15 << 2
        { false, false, DysonPipelineErrorKind.Failed, 4, null, null, null, 120 },  // capped
        { false, false, null, 0, 499, null, null, 120 },        // idle base 120 x 3, capped
        { true, false, null, 0, 100, null, null, 45 },           // 15 x 3, under the cap
        { false, true, null, 0, null, null, 30, 30 },            // poll hint above the active base, under the cap
        { false, false, null, 0, null, null, 500, 120 },         // poll hint capped
        { false, true, null, 0, null, null, 5, 15 },             // hint below the base is ignored
        { false, false, DysonPipelineErrorKind.RateLimited, 0, null, null, null, 60 },      // no reset: at least 60
        { false, false, DysonPipelineErrorKind.RateLimited, 0, null, 30, null, 60 },        // reset sooner: still 60
        { false, false, DysonPipelineErrorKind.RateLimited, 0, null, 300, null, 300 },      // the only case above 120
    };

    [Fact]
    public async Task Two_leases_share_one_loop_and_disposing_both_stops_it()
    {
        var provider = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)] };
        await using var h = Harness.Create(provider, pillIdle: TimeSpan.FromSeconds(120));
        var first = h.Listen();
        var a = h.Service.Watch(false);
        var b = h.Service.Watch(false);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var afterFirst = provider.RunsCalls;
        a.Dispose();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await h.UntilRunsAsync(afterFirst + 1);

        b.Dispose();
        await h.Clock.SettleAsync();
        var stopped = provider.RunsCalls;
        Assert.Equal(0, h.Clock.InDelay);
        await Task.Delay(100);
        Assert.Equal(stopped, provider.RunsCalls);
    }

    [Fact]
    public async Task Not_modified_publishes_nothing_and_a_change_publishes_once()
    {
        var provider = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)], Etag = "e1" };
        await using var h = Harness.Create(provider);
        var events = h.Collect();
        using var lease = h.Service.Watch(false);
        await h.UntilRunsAsync(1);

        provider.NotModified = true;
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await h.UntilRunsAsync(2);
        Assert.Equal(1, events.Count);

        provider.NotModified = false;
        provider.Runs = [Run(1, DysonPipelineState.Running)];
        var changed = h.Listen();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Second_tick_receives_the_etag_from_the_first()
    {
        var provider = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)], Etag = "W/\"1\"" };
        await using var h = Harness.Create(provider);
        using var lease = h.Service.Watch(false);
        await h.UntilRunsAsync(1);
        Assert.Null(provider.RunEtags.ElementAt(0));

        provider.NotModified = true;
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await h.UntilRunsAsync(2);
        Assert.Equal("W/\"1\"", provider.RunEtags.ElementAt(1));
    }

    [Fact]
    public async Task Jobs_are_fetched_only_with_a_panel_lease_and_at_most_three_per_tick()
    {
        var provider = new FakeProvider
        {
            Runs = [Run(1, DysonPipelineState.Running), Run(2, DysonPipelineState.Running),
                    Run(3, DysonPipelineState.Queued), Run(4, DysonPipelineState.Running),
                    Run(5, DysonPipelineState.Succeeded)],
        };
        await using var h = Harness.Create(provider);
        var pillTick = h.Listen();
        using var pill = h.Service.Watch(false);
        await pillTick.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, provider.JobsCalls);

        // The provider call starts the tick; the publish ends it, after the jobs are in the snapshot.
        var panelTick = h.Listen();
        using var panel = h.Service.Watch(true);
        await panelTick.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, provider.JobsCalls);
        Assert.Equal(3, h.Service.Snapshot!.Jobs.Count);

        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(15));
        await h.UntilJobsAsync(4);
        Assert.Equal([4L], provider.JobRunIds.Skip(3).ToArray());
    }

    [Fact]
    public async Task CliMissing_stays_detected_and_refresh_now_polls_again()
    {
        var provider = new FakeProvider
        {
            Error = new DysonPipelineError(DysonPipelineErrorKind.CliMissing, "gh not found"),
        };
        await using var h = Harness.Create(provider);
        var published = h.Listen();
        using var lease = h.Service.Watch(false);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = h.Service.Snapshot!;
        Assert.True(snapshot.IsDetected);
        Assert.Equal(DysonPipelineErrorKind.CliMissing, snapshot.Error!.Kind);

        var before = provider.RunsCalls;
        h.Service.RefreshNow();
        await h.UntilRunsAsync(before + 1);
        Assert.True(provider.ProbeResets > 0 || provider.RunsCalls > before);
    }

    [Fact]
    public async Task Non_github_origin_is_not_detected_and_the_loop_ends()
    {
        var provider = new FakeProvider { Resolve = false };
        await using var h = Harness.Create(provider, origin: "https://gitlab.com/o/r.git");
        var published = h.Listen();
        using var lease = h.Service.Watch(false);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(h.Service.Snapshot!.IsDetected);
        Assert.Equal(DysonPipelineErrorKind.NotDetected, h.Service.Snapshot.Error!.Kind);
        Assert.Equal(0, provider.RunsCalls);
        await h.Clock.SettleAsync();
        Assert.Equal(0, h.Clock.Pending);
        Assert.Equal(0, provider.RunsCalls);
    }

    [Fact]
    public async Task Error_after_success_keeps_the_last_runs()
    {
        var runs = new List<DysonPipelineRun> { Run(1, DysonPipelineState.Succeeded) };
        var provider = new FakeProvider { Runs = runs, Etag = "e" };
        await using var h = Harness.Create(provider);
        var first = h.Listen();
        using var lease = h.Service.Watch(false);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        provider.Error = new DysonPipelineError(DysonPipelineErrorKind.Failed, "boom");
        var published = h.Listen();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = h.Service.Snapshot!;
        Assert.Equal(runs, snapshot.Runs);
        Assert.Equal(DysonPipelineErrorKind.Failed, snapshot.Error!.Kind);
    }

    [Fact]
    public async Task Debounce_collapses_three_calls_into_one_tick_five_seconds_after_the_last()
    {
        var provider = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)], Etag = "e" };
        await using var h = Harness.Create(provider);
        h.Service.RequestPollSoon();
        h.Service.RequestPollSoon();
        h.Service.RequestPollSoon();

        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilRunsAsync(1);
        Assert.Equal(1, provider.RunsCalls);
        await h.Clock.SettleAsync();
        Assert.Equal(0, h.Clock.Pending);

        h.Service.RequestPollSoon();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilRunsAsync(2);
    }

    [Fact]
    public async Task Without_leases_a_poll_request_runs_exactly_one_tick()
    {
        var provider = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)] };
        await using var h = Harness.Create(provider);
        h.Service.RequestPollSoon();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilRunsAsync(1);
        await h.Clock.SettleAsync();
        Assert.Equal(0, h.Clock.Pending);
        Assert.Equal(1, provider.RunsCalls);
    }

    [Fact]
    public async Task With_leases_the_debounce_wakes_the_existing_loop_without_reentry()
    {
        var provider = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)] };
        await using var h = Harness.Create(provider);
        using var lease = h.Service.Watch(false);
        await h.UntilRunsAsync(1);

        provider.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.RequestPollSoon();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilRunsAsync(2);
        Assert.Equal(1, provider.MaxInFlight);

        provider.Gate.SetResult();
        await h.UntilRunsAsync(2);
        Assert.Equal(1, provider.MaxInFlight);
    }

    [Fact]
    public async Task Debounced_tick_passes_the_stored_etag()
    {
        var provider = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)], Etag = "stored" };
        await using var h = Harness.Create(provider);
        h.Service.RequestPollSoon();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilRunsAsync(1);

        provider.NotModified = true;
        h.Service.RequestPollSoon();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilRunsAsync(2);
        Assert.Equal("stored", provider.RunEtags.ElementAt(1));
    }

    [Fact]
    public async Task Refresh_now_re_detects_after_not_detected()
    {
        var provider = new FakeProvider { Resolve = false };
        await using var h = Harness.Create(provider, origin: "https://github.com/o/r.git");
        var published = h.Listen();
        using var lease = h.Service.Watch(false);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(h.Service.Snapshot!.IsDetected);

        provider.Resolve = true; // e.g. a GitHub remote was added afterwards
        h.Service.RefreshNow();
        await h.UntilRunsAsync(1);
        await h.UntilAsync(() => h.Service.Snapshot?.IsDetected == true);
    }

    [Fact]
    public async Task Not_detected_instance_makes_no_provider_calls_on_poll_soon()
    {
        var provider = new FakeProvider { Resolve = false };
        await using var h = Harness.Create(provider, origin: "https://gitlab.com/o/r.git");
        var published = h.Listen();
        h.Service.RequestPollSoon();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, provider.RunsCalls);

        h.Service.RequestPollSoon();
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, provider.RunsCalls);
    }

    [Fact]
    public async Task Bus_event_debounces_per_service_and_never_crosses_work_directories()
    {
        var a = new FakeProvider { Runs = [Run(1, DysonPipelineState.Succeeded)] };
        var b = new FakeProvider { Runs = [Run(9, DysonPipelineState.Succeeded)] };
        await using var h = Harness.Pair(a, b);

        h.Bus.Publish(DysonBusScopes.WorkDirectory(h.IdA), new DysonShellVcsCommandRanEvent(h.IdA));
        h.Bus.Publish(DysonBusScopes.WorkDirectory(h.IdA), new DysonShellVcsCommandRanEvent(h.IdA));
        h.Bus.Publish(DysonBusScopes.WorkDirectory(h.IdA), new DysonShellVcsCommandRanEvent(h.IdA));
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilAsync(() => a.RunsCalls == 1);
        Assert.Equal(1, a.RunsCalls);
        Assert.Equal(0, b.RunsCalls);

        h.Bus.Publish(DysonBusScopes.WorkDirectory(h.IdB), new DysonShellVcsCommandRanEvent(h.IdA));
        await h.Clock.SettleAsync();
        Assert.Equal(0, h.Clock.InDelay);
        Assert.Equal(1, a.RunsCalls);
        Assert.Equal(0, b.RunsCalls);
        await Task.Delay(300);
        Assert.Equal(0, h.Clock.InDelay);
        Assert.Equal(1, a.RunsCalls);
        Assert.Equal(0, b.RunsCalls);
    }

    [Fact]
    public async Task Bus_event_for_an_unknown_work_directory_creates_nothing()
    {
        var provider = new FakeProvider();
        var clock = new FakeClock();
        using var bus = new DysonMessageBus();
        using var registry = new DysonVersionControlRegistry(bus, [() => provider], clock.Seams("https://github.com/o/a"));
        var unknown = Guid.NewGuid();

        bus.Publish(DysonBusScopes.WorkDirectory(unknown), new DysonShellVcsCommandRanEvent(unknown));
        await clock.SettleAsync();

        Assert.False(registry.TryGet(unknown, out _));
        Assert.Equal(0, provider.RunsCalls);
    }

    private static DysonPipelineRun Run(long id, DysonPipelineState state) => new(
        id, "ci", "title", "main", "abcdef1234567", "push", (int)id, 1, state, state.ToString(),
        T0, T0, T0, $"https://github.com/o/r/actions/runs/{id}");

    private sealed class Harness : IAsyncDisposable
    {
        public DysonMessageBus Bus { get; }
        public DysonVersionControlRegistry Registry { get; }
        public DysonVersionControlService Service { get; }
        public FakeClock Clock { get; }
        public Guid IdA { get; private set; }
        public Guid IdB { get; private set; }
        private readonly ConcurrentQueue<DysonPipelinesChangedEvent> _events = new();
        private readonly List<IDisposable> _subs = new();

        private Harness(DysonMessageBus bus, DysonVersionControlRegistry registry, DysonVersionControlService service, FakeClock clock)
        {
            Bus = bus;
            Registry = registry;
            Service = service;
            Clock = clock;
        }

        public static Harness Create(FakeProvider provider, string origin = "https://github.com/o/r.git", TimeSpan? pillIdle = null)
        {
            var clock = new FakeClock();
            var bus = new DysonMessageBus();
            var registry = new DysonVersionControlRegistry(bus, [() => provider], clock.Seams(origin));
            var entity = Entity(Guid.NewGuid(), origin);
            return new Harness(bus, registry, registry.GetOrCreate(entity).Value, clock) { IdA = entity.Id };
        }

        public static Harness Pair(FakeProvider a, FakeProvider b)
        {
            var clock = new FakeClock();
            var bus = new DysonMessageBus();
            var next = 0;
            FakeProvider[] providers = [a, b];
            var registry = new DysonVersionControlRegistry(bus, [() => providers[next++]], clock.Seams("https://github.com/o/a"));
            var entityA = Entity(Guid.NewGuid(), "https://github.com/o/a");
            var entityB = Entity(Guid.NewGuid(), "https://github.com/o/b");
            return new Harness(bus, registry, registry.GetOrCreate(entityA).Value, clock)
            {
                IdA = entityA.Id,
                IdB = entityB.Id,
            }.Also(entityB);
        }

        private Harness Also(DysonWorkDirectoryEntity entityB)
        {
            Registry.GetOrCreate(entityB);
            return this;
        }

        public TaskCompletionSource<DysonPipelinesChangedEvent> Listen()
        {
            var tcs = new TaskCompletionSource<DysonPipelinesChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _subs.Add(Bus.Subscribe<DysonPipelinesChangedEvent>(DysonBusScopes.WorkDirectory(Service.WorkDirectoryId), e => tcs.TrySetResult(e)).Value);
            return tcs;
        }

        public ConcurrentQueue<DysonPipelinesChangedEvent> Collect()
        {
            _subs.Add(Bus.Subscribe<DysonPipelinesChangedEvent>(DysonBusScopes.WorkDirectory(Service.WorkDirectoryId), _events.Enqueue).Value);
            return _events;
        }

        /// <summary>A tick has started its runs call. It has not published yet: await <see cref="Listen"/> to read its snapshot.</summary>
        public Task UntilRunsAsync(int count) => UntilAsync(() => Field() is FakeProvider provider && provider.RunsCalls >= count);

        public Task UntilJobsAsync(int count) => UntilAsync(() => Field() is FakeProvider provider && provider.JobsCalls >= count);

        private IVersionControlProvider? Field() =>
            (IVersionControlProvider)typeof(DysonVersionControlService)
                .GetField("_provider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(Service)!;

        public async Task UntilAsync(Func<bool> done)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!done() && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            Assert.True(done());
        }

        public ValueTask DisposeAsync()
        {
            foreach (var sub in _subs)
                sub.Dispose();
            Registry.Dispose();
            Bus.Dispose();
            return ValueTask.CompletedTask;
        }

        private static DysonWorkDirectoryEntity Entity(Guid id, string origin) => new()
        {
            Id = id, SubjectId = "local", AbsolutePath = "E:/repo", GitOrigin = origin,
        };
    }

    private sealed class FakeClock
    {
        private readonly ConcurrentQueue<PendingDelay> _pending = new();
    private int _waiting;

        public DysonVersionControlSeams Seams(string origin) => new(
            (span, ct) => Delay(span, ct),
            () => T0,
            (_, _) => Task.FromResult(Result<string, string>.AsValue(origin)));

        /// <summary>Releases one parked delay of <paramref name="span"/>. A cancelled wait is skipped, not failed.</summary>
        public int Pending => _pending.Count + Volatile.Read(ref _waiting);

        public int InDelay => Volatile.Read(ref _waiting);

        public async Task SettleAsync()
        {
            var quiet = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (Volatile.Read(ref _waiting) == 0 && _pending.IsEmpty)
                {
                    if (DateTime.UtcNow >= quiet)
                        return;
                }
                else
                    quiet = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
                await Task.Delay(10);
            }
        }

        public async Task AdvanceOneAsync(TimeSpan span)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var skipped = new List<PendingDelay>();
            while (true)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("no pending delay");
                if (_pending.TryDequeue(out var pending))
                {
                    if (pending.Done.Task.IsCompleted)
                        continue;
                    if (pending.Span != span)
                    {
                        skipped.Add(pending);
                        continue;
                    }

                    foreach (var other in skipped)
                        _pending.Enqueue(other);
                    pending.Done.TrySetResult();
                    var gone = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    while (Volatile.Read(ref _waiting) > 0 && DateTime.UtcNow < gone)
                        await Task.Delay(5);
                    return;
                }

                await Task.Delay(5);
            }
        }

        private async Task Delay(TimeSpan span, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            _pending.Enqueue(new PendingDelay(span, done));
            Interlocked.Increment(ref _waiting);
            try
            {
                await done.Task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        private sealed record PendingDelay(TimeSpan Span, TaskCompletionSource Done);
    }

    private sealed class FakeProvider : IVersionControlProvider
    {
        private int _inFlight;

        public List<DysonPipelineRun> Runs { get; set; } = new();
        public bool NotModified { get; set; }
        public string? Etag { get; set; }
        public DysonPipelineError? Error { get; set; }
        public bool Resolve { get; set; } = true;
        public TaskCompletionSource? Gate { get; set; }
        public int RunsCalls => RunEtags.Count;
        public int JobsCalls => JobRunIds.Count;
        public ConcurrentQueue<string?> RunEtags { get; } = new();
        public ConcurrentQueue<long> JobRunIds { get; } = new();
        public int MaxInFlight { get; private set; }
        public int ProbeResets { get; private set; }

        public string Id => "github";
        public string DisplayName => "GitHub";

        public void ResetProbe() => ProbeResets++;

        public Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin) =>
            Resolve
                ? Result<DysonPipelineTarget, DysonPipelineError>.AsValue(
                    new DysonPipelineTarget("github.com", "o", origin.TrimEnd('/').Split('/')[^1].Replace(".git", ""), origin))
                : Result<DysonPipelineTarget, DysonPipelineError>.AsError(
                    new DysonPipelineError(DysonPipelineErrorKind.NotDetected, "no"));

        public async Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
            DysonPipelineTarget target, string? etag, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            MaxInFlight = Math.Max(MaxInFlight, now);
            try
            {
                // Read the knobs before recording the call: a test that saw this call may set them for the next one.
                var (gate, error, notModified, runs, nextEtag) = (Gate, Error, NotModified, Runs, Etag);
                RunEtags.Enqueue(etag);
                if (gate is not null)
                    await gate.Task.WaitAsync(ct);
                if (error is not null)
                    return Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>.AsError(error);
                return Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>.AsValue(
                    new DysonPipelineFetch<DysonPipelineRun>(notModified, notModified ? [] : runs, nextEtag, 4000, null));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
            DysonPipelineTarget target, long runId, string? etag, CancellationToken ct)
        {
            JobRunIds.Enqueue(runId);
            IReadOnlyList<DysonPipelineJob> jobs =
            [
                new(runId * 10, runId, "build", DysonPipelineState.Running, "Running", T0, null, "https://github.com/o/r/job"),
            ];
            return Task.FromResult(Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineJob>(false, jobs, "j" + runId, null, null)));
        }
    }
}

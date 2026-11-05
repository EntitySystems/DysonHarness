namespace DysonHarness.Tests;

public sealed class DysonVersionControlRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_work_directories_get_separate_instances_state_and_events()
    {
        var a = new RecordingProvider("a");
        var b = new RecordingProvider("b");
        var next = 0;
        RecordingProvider[] created = [a, b];
        await using var h = Rig.Open([() => created[next++]]);

        var entityA = Rig.Entity("a", "https://github.com/o/a");
        var entityB = Rig.Entity("b", "https://github.com/o/b");
        var firstA = h.Registry.GetOrCreate(entityA);
        var firstB = h.Registry.GetOrCreate(entityB);
        var secondA = h.Registry.GetOrCreate(entityA);
        Assert.True(firstA.IsSuccess && firstB.IsSuccess);
        Assert.NotSame(firstA.Value, firstB.Value);
        Assert.Same(firstA.Value, secondA.Value);

        var eventsA = new List<Guid>();
        var eventsB = new List<Guid>();
        using var subA = h.Bus.Subscribe<DysonPipelinesChangedEvent>(DysonBusScopes.WorkDirectory(entityA.Id), e => eventsA.Add(e.WorkDirectoryId)).Value;
        using var subB = h.Bus.Subscribe<DysonPipelinesChangedEvent>(DysonBusScopes.WorkDirectory(entityB.Id), e => eventsB.Add(e.WorkDirectoryId)).Value;

        using var leaseA = firstA.Value.Watch(false);
        using var leaseB = firstB.Value.Watch(false);
        // Both loops parked on their idle wait; otherwise a fast `a` can park twice and take both releases.
        await h.UntilAsync(() => h.Clock.Parked == 2);
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await h.UntilAsync(() => a.Calls.Count >= 2 && b.Calls.Count >= 2);

        Assert.Equal(("o", "a", "e-a"), (a.Calls[1].Owner, a.Calls[1].Name, a.Calls[1].Etag));
        Assert.Equal(("o", "b", "e-b"), (b.Calls[1].Owner, b.Calls[1].Name, b.Calls[1].Etag));
        Assert.DoesNotContain(firstA.Value.Snapshot!.Runs, run => run.Id == 2);
        Assert.Contains(firstA.Value.Snapshot!.Runs, run => run.Id == 1);
        Assert.Equal(2, next);
        Assert.All(eventsA, id => Assert.Equal(entityA.Id, id));
        Assert.All(eventsB, id => Assert.Equal(entityB.Id, id));
        Assert.NotEmpty(eventsA);
    }

    [Fact]
    public async Task Hook_for_a_never_polls_b()
    {
        var a = new RecordingProvider("a");
        var b = new RecordingProvider("b");
        var next = 0;
        RecordingProvider[] created = [a, b];
        await using var h = Rig.Open([() => created[next++]]);
        var serviceA = h.Registry.GetOrCreate(Rig.Entity("a", "https://github.com/o/a")).Value;
        var serviceB = h.Registry.GetOrCreate(Rig.Entity("b", "https://github.com/o/b")).Value;

        h.Bus.Publish(DysonBusScopes.WorkDirectory(serviceA.WorkDirectoryId), new DysonShellVcsCommandRanEvent(serviceA.WorkDirectoryId));
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(5));
        await h.UntilAsync(() => a.Calls.Count == 1);
        Assert.Empty(b.Calls);
        _ = serviceB;
    }

    [Fact]
    public async Task Event_for_an_unknown_id_creates_nothing()
    {
        var provider = new RecordingProvider("a");
        var calls = 0;
        await using var h = Rig.Open([() => { calls++; return provider; }]);
        var unknown = Guid.NewGuid();

        h.Bus.Publish(DysonBusScopes.WorkDirectory(unknown), new DysonShellVcsCommandRanEvent(unknown));
        await Task.Delay(30);
        Assert.False(h.Registry.TryGet(unknown, out _));
        Assert.Equal(0, calls);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public void Empty_factory_list_returns_an_error()
    {
        using var h = Rig.Open([]).AsSync();
        var result = h.Registry.GetOrCreate(Rig.Entity("a", "https://github.com/o/a"));
        Assert.True(result.IsError);
    }

    [Fact]
    public void Subject_mismatch_returns_an_error()
    {
        using var h = Rig.Open([() => new RecordingProvider("a")]).AsSync();
        var entity = Rig.Entity("a", "https://github.com/o/a");
        Assert.True(h.Registry.GetOrCreate(entity).IsSuccess);

        entity.SubjectId = "other-subject";
        var again = h.Registry.GetOrCreate(entity);
        Assert.True(again.IsError);
    }

    [Fact]
    public async Task Remove_stops_only_that_instance()
    {
        var a = new RecordingProvider("a");
        var b = new RecordingProvider("b");
        var next = 0;
        RecordingProvider[] created = [a, b];
        await using var h = Rig.Open([() => created[next++]]);
        var serviceA = h.Registry.GetOrCreate(Rig.Entity("a", "https://github.com/o/a")).Value;
        var serviceB = h.Registry.GetOrCreate(Rig.Entity("b", "https://github.com/o/b")).Value;
        using var leaseA = serviceA.Watch(false);
        using var leaseB = serviceB.Watch(false);
        await h.UntilAsync(() => a.Calls.Count >= 1 && b.Calls.Count >= 1);

        h.Registry.Remove(serviceA.WorkDirectoryId);
        var stopped = a.Calls.Count;
        await h.Clock.AdvanceOneAsync(TimeSpan.FromSeconds(120));
        await h.UntilAsync(() => b.Calls.Count >= 2);
        await Task.Delay(50);
        Assert.Equal(stopped, a.Calls.Count);
        Assert.False(h.Registry.TryGet(serviceA.WorkDirectoryId, out _));
    }

    [Fact]
    public async Task Dispose_stops_every_instance()
    {
        var a = new RecordingProvider("a");
        var b = new RecordingProvider("b");
        var next = 0;
        RecordingProvider[] created = [a, b];
        var h = Rig.Open([() => created[next++]]);
        var serviceA = h.Registry.GetOrCreate(Rig.Entity("a", "https://github.com/o/a")).Value;
        var serviceB = h.Registry.GetOrCreate(Rig.Entity("b", "https://github.com/o/b")).Value;
        using var leaseA = serviceA.Watch(false);
        using var leaseB = serviceB.Watch(false);
        await h.UntilAsync(() => a.Calls.Count >= 1 && b.Calls.Count >= 1);

        h.Registry.Dispose();
        var callsA = a.Calls.Count;
        var callsB = b.Calls.Count;
        await Task.Delay(50);
        Assert.Equal(callsA, a.Calls.Count);
        Assert.Equal(callsB, b.Calls.Count);
        Assert.False(h.Registry.TryGet(serviceA.WorkDirectoryId, out _));
        h.Bus.Dispose();
    }

    private static class Rig
    {
        public static Handle Open(IEnumerable<DysonVersionControlProviderFactory> factories)
        {
            var clock = new Clock();
            var bus = new DysonMessageBus();
            var registry = new DysonVersionControlRegistry(bus, factories, clock.Seams);
            return new Handle(bus, registry, clock);
        }

        public static DysonWorkDirectoryEntity Entity(string name, string origin) => new()
        {
            Id = Guid.NewGuid(), SubjectId = "local", AbsolutePath = "E:/" + name, GitOrigin = origin,
        };
    }

    private sealed class Handle(DysonMessageBus bus, DysonVersionControlRegistry registry, Clock clock) : IDisposable, IAsyncDisposable
    {
        public Handle AsSync() => this;

        public DysonMessageBus Bus { get; } = bus;
        public DysonVersionControlRegistry Registry { get; } = registry;
        public Clock Clock { get; } = clock;

        public async Task UntilAsync(Func<bool> done)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!done() && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            Assert.True(done());
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public ValueTask DisposeAsync()
        {
            Registry.Dispose();
            Bus.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Clock
    {
        private readonly Queue<Pending> _pending = new();
        private readonly object _gate = new();

        public DysonVersionControlSeams Seams => new(
            (span, ct) => Delay(span, ct), () => T0, (_, _) => Task.FromResult(Result<string, string>.AsValue("")));

        public int Parked
        {
            get
            {
                lock (_gate)
                    return _pending.Count(p => !p.Done.Task.IsCompleted);
            }
        }

        public async Task AdvanceOneAsync(TimeSpan span)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var skipped = new List<Pending>();
            Pending pending;
            while (true)
            {
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        pending = _pending.Dequeue();
                        if (pending.Done.Task.IsCompleted || pending.Span != span)
                        {
                            skipped.Add(pending);
                            continue;
                        }

                        foreach (var other in skipped)
                            _pending.Enqueue(other);
                        break;
                    }
                }

                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("no pending delay");
                await Task.Delay(5);
            }

            pending.Done.TrySetResult();
            await Task.Delay(20);
        }

        private Task Delay(TimeSpan span, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            lock (_gate)
                _pending.Enqueue(new Pending(span, done));
            return done.Task;
        }

        private sealed record Pending(TimeSpan Span, TaskCompletionSource Done);
    }

    private sealed class RecordingProvider(string repo) : IVersionControlProvider
    {
        public List<(string Owner, string Name, string? Etag)> Calls { get; } = new();

        public string Id => "github";
        public string DisplayName => "GitHub";

        public Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin)
        {
            var name = origin.TrimEnd('/').Split('/')[^1].Replace(".git", "");
            return Result<DysonPipelineTarget, DysonPipelineError>.AsValue(
                new DysonPipelineTarget("github.com", "o", name, origin));
        }

        public Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
            DysonPipelineTarget target, string? etag, CancellationToken ct)
        {
            Calls.Add((target.Owner, target.Name, etag));
            IReadOnlyList<DysonPipelineRun> runs = etag is null
                ? [new(repo == "a" ? 1 : 2, "ci", "t", "main", "abcdef1", "push", 1, 1,
                    DysonPipelineState.Succeeded, "Succeeded", T0, T0, T0, "https://github.com/o/" + repo)]
                : [];
            return Task.FromResult(Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineRun>(etag is not null, runs, "e-" + repo, 4000, null)));
        }

        public Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
            DysonPipelineTarget target, long runId, string? etag, CancellationToken ct) =>
            Task.FromResult(Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>.AsValue(
                new DysonPipelineFetch<DysonPipelineJob>(true, [], null, null, null)));
    }
}

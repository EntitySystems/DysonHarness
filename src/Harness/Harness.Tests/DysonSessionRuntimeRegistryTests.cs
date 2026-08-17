using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: subject-keyed registry shares one retained runtime; circuit drop must not dispose it.
/// </summary>
public class DysonSessionRuntimeRegistryTests
{
    [Fact]
    public async Task Same_subject_shares_one_runtime()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");

        var first = await harness.Registry.GetOrCreateAsync(subject);
        var second = await harness.Registry.GetOrCreateAsync(subject);

        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Same(first.Value, second.Value);
        Assert.Equal(1, harness.Factory.CreateCalls);
        Assert.NotNull(first.Value.LastRecoveryReport);
        Assert.Equal(0, first.Value.LastRecoveryReport.UnfinishedSessions);
        Assert.True(harness.Registry.TryGet(subject, out var found));
        Assert.Same(first.Value, found);
    }

    [Fact]
    public async Task Guid_subject_normalizes_to_canonical_form()
    {
        await using var harness = await Harness.CreateAsync();
        var guid = Guid.NewGuid();

        var created = await harness.Registry.GetOrCreateAsync(guid.ToString("N"));
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        Assert.Equal(guid.ToString("D"), created.Value.SubjectId);
        Assert.True(harness.Registry.TryGet(guid.ToString("B"), out var found));
        Assert.Same(created.Value, found);
    }

    [Fact]
    public async Task Different_subjects_are_isolated()
    {
        await using var harness = await Harness.CreateAsync();
        var subjectA = Guid.NewGuid().ToString("D");
        var subjectB = Guid.NewGuid().ToString("D");

        var runtimeA = await harness.Registry.GetOrCreateAsync(subjectA);
        var runtimeB = await harness.Registry.GetOrCreateAsync(subjectB);
        Assert.True(runtimeA.IsSuccess, runtimeA.IsError ? runtimeA.Error : null);
        Assert.True(runtimeB.IsSuccess, runtimeB.IsError ? runtimeB.Error : null);
        Assert.NotSame(runtimeA.Value, runtimeB.Value);
        Assert.Equal(2, harness.Factory.CreateCalls);

        runtimeA.Value.ReportError("subject-a failed");
        Assert.Equal("subject-a failed", runtimeA.Value.LastError);
        Assert.Null(runtimeB.Value.LastError);

        var missing = await runtimeB.Value.GetSessionAsync(Guid.NewGuid());
        Assert.True(missing.IsError);
        Assert.Contains("not found", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(runtimeA.Value.IsBusy(Guid.NewGuid()));
        Assert.Equal(0, runtimeB.Value.GetQueuedPromptCount(Guid.NewGuid()));
    }

    [Fact]
    public async Task Concurrent_same_subject_creates_once()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");

        var tasks = Enumerable
            .Range(0, 16)
            .Select(_ => harness.Registry.GetOrCreateAsync(subject))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.IsError ? result.Error : null));
        var first = results[0].Value;
        Assert.All(results, result => Assert.Same(first, result.Value));
        Assert.Equal(1, harness.Factory.CreateCalls);
    }

    [Fact]
    public async Task Dropping_a_circuit_reference_does_not_dispose_retained_scope()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");

        var first = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        var circuitRuntime = first.Value;
        circuitRuntime = null;
        GC.KeepAlive(circuitRuntime);

        var second = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Same(first.Value, second.Value);
        Assert.Equal(1, harness.Factory.CreateCalls);
        Assert.Equal(0, harness.Factory.DisposeCalls);
        Assert.False(second.Value.TryGetSession(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task Disposing_circuit_change_subscriber_does_not_dispose_in_progress_retained_runtime()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");

        var created = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var runtime = created.Value;

        var circuitChanges = new List<DysonRuntimeChange>();
        EventHandler<DysonRuntimeChange> circuitHandler = (_, change) => circuitChanges.Add(change);
        runtime.Changed += circuitHandler;

        runtime.ReportError("in-progress");
        Assert.Equal("in-progress", runtime.LastError);
        var inProgress = Assert.Single(circuitChanges);
        Assert.Equal(DysonRuntimeChangeKind.Error, inProgress.Kind);
        Assert.Equal(subject, inProgress.SubjectId);

        runtime.Changed -= circuitHandler;

        Assert.Equal(0, harness.Factory.DisposeCalls);
        Assert.True(harness.Registry.TryGet(subject, out var retained));
        Assert.Same(runtime, retained);

        var afterCircuit = await runtime.GetSessionAsync(Guid.NewGuid());
        Assert.True(afterCircuit.IsError);
        Assert.Contains("not found", afterCircuit.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disposed", afterCircuit.Error, StringComparison.OrdinalIgnoreCase);

        DysonRuntimeChange? afterDisconnect = null;
        EventHandler<DysonRuntimeChange> remaining = (_, change) => afterDisconnect = change;
        runtime.Changed += remaining;
        runtime.ReportError("still-alive");
        runtime.Changed -= remaining;

        Assert.Equal("still-alive", runtime.LastError);
        Assert.NotNull(afterDisconnect);
        Assert.Equal(DysonRuntimeChangeKind.Error, afterDisconnect.Kind);
        Assert.Single(circuitChanges);
        Assert.Equal(1, harness.Factory.CreateCalls);
        Assert.Equal(0, harness.Factory.DisposeCalls);

        await harness.Registry.DisposeAsync();

        Assert.Equal(1, harness.Factory.DisposeCalls);
        var afterShutdown = await runtime.GetSessionAsync(Guid.NewGuid());
        Assert.True(afterShutdown.IsError);
        Assert.Contains("disposed", afterShutdown.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Registry.TryGet(subject, out _));
        runtime.ReportError("after-shutdown");
        Assert.Null(runtime.LastError);
    }

    [Fact]
    public async Task Registry_dispose_disposes_retained_scope()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");

        var created = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var runtime = created.Value;

        await harness.Registry.DisposeAsync();

        Assert.Equal(1, harness.Factory.DisposeCalls);
        var after = await runtime.GetSessionAsync(Guid.NewGuid());
        Assert.True(after.IsError);
        Assert.Contains("disposed", after.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Registry.TryGet(subject, out _));

        var recreate = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(recreate.IsError);
        Assert.Contains("disposed", recreate.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_subject_identity_does_not_follow_rebound_context()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");
        var other = Guid.NewGuid().ToString("D");
        var context = new DysonTempDb.MutableSubjectContext(subject);

        await using var runtime = new DysonSessionRuntime(
            context,
            harness.Sessions,
            new UnusedSessionFactory());

        context.SubjectId = other;
        Assert.Equal(subject, runtime.SubjectId);
        Assert.NotEqual(other, runtime.SubjectId);
    }

    [Fact]
    public async Task ReportError_raises_change_for_that_subject_only()
    {
        await using var harness = await Harness.CreateAsync();
        var subjectA = Guid.NewGuid().ToString("D");
        var subjectB = Guid.NewGuid().ToString("D");
        var runtimeA = (await harness.Registry.GetOrCreateAsync(subjectA)).Value;
        var runtimeB = (await harness.Registry.GetOrCreateAsync(subjectB)).Value;

        DysonRuntimeChange? seenA = null;
        DysonRuntimeChange? seenB = null;
        runtimeA.Changed += (_, change) => seenA = change;
        runtimeB.Changed += (_, change) => seenB = change;

        runtimeA.ReportError("boom");

        Assert.NotNull(seenA);
        Assert.Null(seenB);
        Assert.Equal(subjectA, seenA.SubjectId);
        Assert.Equal(DysonRuntimeChangeKind.Error, seenA.Kind);
        Assert.True(seenA.Version > 0);
        Assert.Null(seenA.SessionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(DysonSubjects.Shared)]
    [InlineData("not-a-subject")]
    public async Task Invalid_subject_is_rejected(string? subjectId)
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Registry.GetOrCreateAsync(subjectId!);
        Assert.True(created.IsError);
        Assert.False(harness.Registry.TryGet(subjectId!, out _));
        Assert.Equal(0, harness.Factory.CreateCalls);
    }

    [Fact]
    public async Task Factory_error_is_not_cached()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");
        harness.Factory.FailNext = "scope bind failed";

        var first = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(first.IsError);
        Assert.Equal("scope bind failed", first.Error);
        Assert.False(harness.Registry.TryGet(subject, out _));

        var second = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Equal(2, harness.Factory.CreateCalls);
    }

    [Fact]
    public async Task Local_subject_is_accepted()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Registry.GetOrCreateAsync(DysonSubjects.Local);
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        Assert.Equal(DysonSubjects.Local, created.Value.SubjectId);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(
            SqliteConnection connection,
            IDysonSessionRepository sessions,
            CountingScopeFactory factory,
            DysonSessionRuntimeRegistry registry)
        {
            _connection = connection;
            Sessions = sessions;
            Factory = factory;
            Registry = registry;
        }

        public IDysonSessionRepository Sessions { get; }
        public CountingScopeFactory Factory { get; }
        public DysonSessionRuntimeRegistry Registry { get; }

        public static Task<Harness> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var sessions = DysonTempDb.Sessions(accessor);
            var factory = new CountingScopeFactory(sessions);
            var registry = new DysonSessionRuntimeRegistry(factory);
            return Task.FromResult(new Harness(connection, sessions, factory, registry));
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class CountingScopeFactory(IDysonSessionRepository sessions) : IDysonSessionRuntimeScopeFactory
    {
        private readonly IDysonAgentSessionRuntimeFactory _sessionFactory = new UnusedSessionFactory();

        public int CreateCalls;
        public int DisposeCalls;
        public string? FailNext;

        public Task<Result<RuntimeScopeLease, string>> CreateAsync(
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Interlocked.Increment(ref CreateCalls);
            if (FailNext is { } error)
            {
                FailNext = null;
                return Task.FromResult(Result<RuntimeScopeLease, string>.AsError(error));
            }

            var runtime = new DysonSessionRuntime(
                new DysonTempDb.MutableSubjectContext(subjectId),
                sessions,
                _sessionFactory);
            var lease = new RuntimeScopeLease(
                subjectId,
                runtime,
                () =>
                {
                    Interlocked.Increment(ref DisposeCalls);
                    return ValueTask.CompletedTask;
                });
            return Task.FromResult(Result<RuntimeScopeLease, string>.AsValue(lease));
        }
    }

    private sealed class UnusedSessionFactory : IDysonAgentSessionRuntimeFactory
    {
        public Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
            DysonAgentSessionRuntimeCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(
                Result<DysonAgentSessionRuntimeLease, string>.AsError("Session factory is unused in registry tests."));
        }

        public Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            _ = sessionId;
            _ = cancellationToken;
            return Task.FromResult(
                Result<DysonAgentSessionRuntimeLease, string>.AsError("Session factory is unused in registry tests."));
        }
    }
}

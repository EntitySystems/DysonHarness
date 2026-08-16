using DysonHarness;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ponytail: circuit attachment binds/unbinds a retained runtime without owning it.
/// </summary>
public class DysonUiRuntimeAttachmentTests
{
    [Fact]
    public async Task Attach_uses_normalized_current_subject()
    {
        await using var harness = await Harness.CreateAsync();
        var guid = Guid.NewGuid();
        var context = new DysonTempDb.MutableSubjectContext(guid.ToString("N"));
        await using var attachment = new DysonUiRuntimeAttachment(harness.Registry, context);

        var attached = await attachment.AttachAsync();

        Assert.True(attached.IsSuccess, attached.IsError ? attached.Error : null);
        Assert.Equal(guid.ToString("D"), attached.Value.SubjectId);
        Assert.True(attachment.TryGetRuntime(out var exposed));
        Assert.Same(attached.Value, exposed);
        Assert.Same(attached.Value, attachment.Runtime);
        Assert.Equal(1, harness.Factory.CreateCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(DysonSubjects.Shared)]
    [InlineData("not-a-subject")]
    public async Task Invalid_subject_is_a_result_error(string? subjectId)
    {
        await using var harness = await Harness.CreateAsync();
        var context = new DysonTempDb.MutableSubjectContext(subjectId!);
        await using var attachment = new DysonUiRuntimeAttachment(harness.Registry, context);

        var attached = await attachment.AttachAsync();

        Assert.True(attached.IsError);
        Assert.False(attachment.TryGetRuntime(out _));
        Assert.Null(attachment.Runtime);
        Assert.Equal(0, harness.Factory.CreateCalls);
    }

    [Fact]
    public async Task Registry_failure_is_a_result_error()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Factory.FailNext = "scope bind failed";
        var context = new DysonTempDb.MutableSubjectContext(DysonSubjects.Local);
        await using var attachment = new DysonUiRuntimeAttachment(harness.Registry, context);

        var attached = await attachment.AttachAsync();

        Assert.True(attached.IsError);
        Assert.Equal("scope bind failed", attached.Error);
        Assert.False(attachment.TryGetRuntime(out _));
        Assert.Equal(1, harness.Factory.CreateCalls);
        Assert.Equal(0, harness.Factory.DisposeCalls);
    }

    [Fact]
    public async Task Repeated_attach_is_idempotent()
    {
        await using var harness = await Harness.CreateAsync();
        var context = new DysonTempDb.MutableSubjectContext(DysonSubjects.Local);
        await using var attachment = new DysonUiRuntimeAttachment(harness.Registry, context);
        var changes = new List<DysonRuntimeChange>();
        attachment.Changed += (_, change) => changes.Add(change);

        var first = await attachment.AttachAsync();
        var second = await attachment.AttachAsync();

        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Same(first.Value, second.Value);
        Assert.Equal(1, harness.Factory.CreateCalls);

        first.Value.ReportError("once");
        Assert.Single(changes);
        Assert.Equal(DysonRuntimeChangeKind.Error, changes[0].Kind);
    }

    [Fact]
    public async Task Dispose_unsubscribes_and_leaves_runtime_usable()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = Guid.NewGuid().ToString("D");
        var context = new DysonTempDb.MutableSubjectContext(subject);
        var changes = new List<DysonRuntimeChange>();
        DysonSessionRuntime runtime;

        await using (var attachment = new DysonUiRuntimeAttachment(harness.Registry, context))
        {
            var attached = await attachment.AttachAsync();
            Assert.True(attached.IsSuccess, attached.IsError ? attached.Error : null);
            runtime = attached.Value;
            attachment.Changed += (_, change) => changes.Add(change);
            runtime.ReportError("circuit-attached");
            Assert.Single(changes);
        }

        Assert.Equal(0, harness.Factory.DisposeCalls);
        Assert.True(harness.Registry.TryGet(subject, out var retained));
        Assert.Same(runtime, retained);

        var missing = await runtime.GetSessionAsync(Guid.NewGuid());
        Assert.True(missing.IsError);
        Assert.Contains("not found", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disposed", missing.Error, StringComparison.OrdinalIgnoreCase);

        runtime.ReportError("after-detach");
        Assert.Equal("after-detach", runtime.LastError);
        Assert.Single(changes);

        var second = await harness.Registry.GetOrCreateAsync(subject);
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Same(runtime, second.Value);
        Assert.Equal(1, harness.Factory.CreateCalls);
    }

    [Fact]
    public async Task Attach_after_dispose_is_a_result_error()
    {
        await using var harness = await Harness.CreateAsync();
        var context = new DysonTempDb.MutableSubjectContext(DysonSubjects.Local);
        var attachment = new DysonUiRuntimeAttachment(harness.Registry, context);
        Assert.True((await attachment.AttachAsync()).IsSuccess);
        await attachment.DisposeAsync();

        var again = await attachment.AttachAsync();

        Assert.True(again.IsError);
        Assert.Contains("disposed", again.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(attachment.TryGetRuntime(out _));
        Assert.True(harness.Registry.TryGet(DysonSubjects.Local, out var retained));
        Assert.NotNull(retained);
        var missing = await retained.GetSessionAsync(Guid.NewGuid());
        Assert.DoesNotContain("disposed", missing.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disposing_one_attachment_does_not_stop_another()
    {
        await using var harness = await Harness.CreateAsync();
        var context = new DysonTempDb.MutableSubjectContext(DysonSubjects.Local);
        var firstChanges = new List<DysonRuntimeChange>();
        var secondChanges = new List<DysonRuntimeChange>();

        await using var first = new DysonUiRuntimeAttachment(harness.Registry, context);
        await using var second = new DysonUiRuntimeAttachment(harness.Registry, context);
        first.Changed += (_, change) => firstChanges.Add(change);
        second.Changed += (_, change) => secondChanges.Add(change);

        var a = await first.AttachAsync();
        var b = await second.AttachAsync();
        Assert.True(a.IsSuccess, a.IsError ? a.Error : null);
        Assert.True(b.IsSuccess, b.IsError ? b.Error : null);
        Assert.Same(a.Value, b.Value);

        a.Value.ReportError("both");
        Assert.Single(firstChanges);
        Assert.Single(secondChanges);

        await first.DisposeAsync();
        a.Value.ReportError("second-only");
        Assert.Single(firstChanges);
        Assert.Equal(2, secondChanges.Count);
        Assert.Equal(0, harness.Factory.DisposeCalls);
    }

    [Fact]
    public async Task Circuit_scoped_repository_is_not_captured_by_registry()
    {
        await using var provider = BuildLocalProvider();
        var registry = provider.GetRequiredService<DysonSessionRuntimeRegistry>();

        IDysonSessionRepository circuitRepo;
        DysonSessionRuntime runtime;
        await using (var circuit = provider.CreateAsyncScope())
        {
            circuitRepo = circuit.ServiceProvider.GetRequiredService<IDysonSessionRepository>();
            var attachment = new DysonUiRuntimeAttachment(
                circuit.ServiceProvider.GetRequiredService<DysonSessionRuntimeRegistry>(),
                circuit.ServiceProvider.GetRequiredService<IDysonSubjectContext>());

            var attached = await attachment.AttachAsync();
            Assert.True(attached.IsSuccess, attached.IsError ? attached.Error : null);
            runtime = attached.Value;
            Assert.NotSame(circuitRepo, runtime.Sessions);
            Assert.Equal(DysonSubjects.Local, ((SubjectRecordingSessionRepository)runtime.Sessions).SubjectId);

            await attachment.DisposeAsync();
        }

        Assert.True(registry.TryGet(DysonSubjects.Local, out var retained));
        Assert.NotNull(retained);
        Assert.Same(runtime, retained);
        Assert.NotSame(circuitRepo, retained.Sessions);
        var missing = await retained.GetSessionAsync(Guid.NewGuid());
        Assert.True(missing.IsError);
        Assert.Contains("not found", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disposed", missing.Error, StringComparison.OrdinalIgnoreCase);

        retained.ReportError("after-circuit");
        Assert.Equal("after-circuit", retained.LastError);
        Assert.Same(runtime, (await registry.GetOrCreateAsync(DysonSubjects.Local)).Value);
    }

    private static ServiceProvider BuildLocalProvider()
    {
        var services = new ServiceCollection();
        services.AddDysonLocalHosting();
        services.AddScoped<IDysonSessionRepository, SubjectRecordingSessionRepository>();
        services.AddScoped<IDysonAgentSessionRuntimeFactory, UnusedSessionFactory>();
        services.AddScoped<DysonSessionRuntime>();
        services.AddSingleton<IDysonSessionRuntimeScopeFactory, DysonUiSessionRuntimeScopeFactory>();
        services.AddSingleton<DysonSessionRuntimeRegistry>();
        return services.BuildServiceProvider();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(CountingScopeFactory factory, DysonSessionRuntimeRegistry registry)
        {
            Factory = factory;
            Registry = registry;
        }

        public CountingScopeFactory Factory { get; }
        public DysonSessionRuntimeRegistry Registry { get; }

        public static Task<Harness> CreateAsync()
        {
            var sessions = new UnusedSessionRepository();
            var factory = new CountingScopeFactory(sessions);
            var registry = new DysonSessionRuntimeRegistry(factory);
            return Task.FromResult(new Harness(factory, registry));
        }

        public async ValueTask DisposeAsync() => await Registry.DisposeAsync().ConfigureAwait(false);
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
                Result<DysonAgentSessionRuntimeLease, string>.AsError(
                    "Session factory is unused in attachment tests."));
        }

        public Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            _ = sessionId;
            _ = cancellationToken;
            return Task.FromResult(
                Result<DysonAgentSessionRuntimeLease, string>.AsError(
                    "Session factory is unused in attachment tests."));
        }
    }

    private sealed class UnusedSessionRepository : IDysonSessionRepository
    {
        public Task<Result<Guid, string>> CreateSessionAsync(
            DysonSessionCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Unused<Guid>();

        public Task<VoidResult<string>> UpdateSessionMetaAsync(
            DysonSessionMetaUpdate update,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<VoidResult<string>> UpsertTurnAsync(
            DysonTurnEntity turn,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<VoidResult<string>> AppendLogAsync(
            DysonSessionLogEntry entry,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(
            Guid? workDirectoryId = null,
            bool rootsOnly = true,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionSummary>>();

        public Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListChildSessionsAsync(
            Guid parentSessionId,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionSummary>>();

        public Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Unused<DysonPersistedSession>();

        public Task<Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>>
            ListActiveSessionsWithUnfinishedTurnsAsync(
                CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(
                Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>.AsValue([]));
        }

        public Task<Result<IReadOnlyList<DysonSessionSummary>, string>>
            ListActiveDescendantSessionsAsync(
                CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(
                Result<IReadOnlyList<DysonSessionSummary>, string>.AsValue([]));
        }

        public Task<VoidResult<string>> DeleteSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionTodo>>();

        public Task<Result<DysonSessionTodo, string>> CreateTodoAsync(
            DysonSessionTodoCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Unused<DysonSessionTodo>();

        public Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(
            DysonSessionTodoUpdateRequest request,
            CancellationToken cancellationToken = default) =>
            Unused<DysonSessionTodo>();

        public Task<VoidResult<string>> DeleteTodoAsync(
            Guid sessionId,
            string taskCode,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(
            Guid sessionId,
            IReadOnlyList<DysonSessionTodoReplaceItem> items,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionTodo>>();

        private static Task<Result<T, string>> Unused<T>() =>
            Task.FromResult(Result<T, string>.AsError("unused"));

        private static Task<VoidResult<string>> UnusedVoid() =>
            Task.FromResult(VoidResult<string>.AsError("unused"));
    }

    private sealed class SubjectRecordingSessionRepository : IDysonSessionRepository
    {
        public SubjectRecordingSessionRepository(IDysonSubjectContext subjectContext)
        {
            SubjectId = subjectContext.SubjectId;
        }

        public string SubjectId { get; }

        public Task<Result<Guid, string>> CreateSessionAsync(
            DysonSessionCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Unused<Guid>();

        public Task<VoidResult<string>> UpdateSessionMetaAsync(
            DysonSessionMetaUpdate update,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<VoidResult<string>> UpsertTurnAsync(
            DysonTurnEntity turn,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<VoidResult<string>> AppendLogAsync(
            DysonSessionLogEntry entry,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(
            Guid? workDirectoryId = null,
            bool rootsOnly = true,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionSummary>>();

        public Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListChildSessionsAsync(
            Guid parentSessionId,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionSummary>>();

        public Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Unused<DysonPersistedSession>();

        public Task<Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>>
            ListActiveSessionsWithUnfinishedTurnsAsync(
                CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(
                Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>.AsValue([]));
        }

        public Task<Result<IReadOnlyList<DysonSessionSummary>, string>>
            ListActiveDescendantSessionsAsync(
                CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(
                Result<IReadOnlyList<DysonSessionSummary>, string>.AsValue([]));
        }

        public Task<VoidResult<string>> DeleteSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionTodo>>();

        public Task<Result<DysonSessionTodo, string>> CreateTodoAsync(
            DysonSessionTodoCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Unused<DysonSessionTodo>();

        public Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(
            DysonSessionTodoUpdateRequest request,
            CancellationToken cancellationToken = default) =>
            Unused<DysonSessionTodo>();

        public Task<VoidResult<string>> DeleteTodoAsync(
            Guid sessionId,
            string taskCode,
            CancellationToken cancellationToken = default) =>
            UnusedVoid();

        public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(
            Guid sessionId,
            IReadOnlyList<DysonSessionTodoReplaceItem> items,
            CancellationToken cancellationToken = default) =>
            Unused<IReadOnlyList<DysonSessionTodo>>();

        private static Task<Result<T, string>> Unused<T>() =>
            Task.FromResult(Result<T, string>.AsError("unused"));

        private static Task<VoidResult<string>> UnusedVoid() =>
            Task.FromResult(VoidResult<string>.AsError("unused"));
    }
}

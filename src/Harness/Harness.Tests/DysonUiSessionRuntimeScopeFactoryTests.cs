using DysonHarness;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ponytail: UI retained-subject-scope factory binds Local / Cloud subjects and
/// lease dispose tears down the child scope. Does not exercise DysonUiHost.
/// </summary>
public class DysonUiSessionRuntimeScopeFactoryTests
{
    [Fact]
    public async Task Local_subject_binds_and_resolves_runtime()
    {
        await using var provider = BuildLocalProvider();
        var factory = provider.GetRequiredService<IDysonSessionRuntimeScopeFactory>();

        await using var lease = (await factory.CreateAsync(DysonSubjects.Local)).Value;

        Assert.Equal(DysonSubjects.Local, lease.SubjectId);
        Assert.Equal(DysonSubjects.Local, lease.Runtime.SubjectId);
        Assert.Equal(DysonSubjects.Local, ((SubjectRecordingSessionRepository)lease.Runtime.Sessions).SubjectId);
    }

    [Fact]
    public async Task Local_hosting_rejects_cloud_guid_subject()
    {
        await using var provider = BuildLocalProvider();
        var counting = WrapFactory(provider, out var factory);

        var created = await factory.CreateAsync(Guid.NewGuid().ToString("D"));

        Assert.True(created.IsError);
        Assert.Contains("local subject", created.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counting.Created);
        Assert.Equal(1, counting.Disposed);
    }

    [Fact]
    public async Task Cloud_subject_binds_before_runtime_resolve()
    {
        await using var provider = BuildCloudProvider();
        var factory = provider.GetRequiredService<IDysonSessionRuntimeScopeFactory>();
        var subject = Guid.NewGuid().ToString("D");

        await using var lease = (await factory.CreateAsync(subject)).Value;

        Assert.Equal(subject, lease.SubjectId);
        Assert.Equal(subject, lease.Runtime.SubjectId);
        Assert.Equal(subject, ((SubjectRecordingSessionRepository)lease.Runtime.Sessions).SubjectId);
    }

    [Fact]
    public async Task Cloud_guid_normalizes_before_bind()
    {
        await using var provider = BuildCloudProvider();
        var factory = provider.GetRequiredService<IDysonSessionRuntimeScopeFactory>();
        var guid = Guid.NewGuid();

        await using var lease = (await factory.CreateAsync(guid.ToString("N"))).Value;

        Assert.Equal(guid.ToString("D"), lease.Runtime.SubjectId);
    }

    [Fact]
    public async Task Cloud_rejects_unbound_local_subject_and_disposes_scope()
    {
        await using var provider = BuildCloudProvider();
        var counting = WrapFactory(provider, out var factory);

        var created = await factory.CreateAsync(DysonSubjects.Local);

        Assert.True(created.IsError);
        Assert.Contains("not bound", created.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counting.Created);
        Assert.Equal(1, counting.Disposed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(DysonSubjects.Shared)]
    [InlineData("not-a-subject")]
    public async Task Invalid_subject_does_not_open_a_scope(string? subjectId)
    {
        await using var provider = BuildCloudProvider();
        var counting = WrapFactory(provider, out var factory);

        var created = await factory.CreateAsync(subjectId!);

        Assert.True(created.IsError);
        Assert.Equal(0, counting.Created);
        Assert.Equal(0, counting.Disposed);
    }

    [Fact]
    public async Task Lease_dispose_disposes_runtime_and_retained_scope()
    {
        await using var provider = BuildLocalProvider();
        var counting = WrapFactory(provider, out var factory);

        var created = await factory.CreateAsync(DysonSubjects.Local);
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var runtime = created.Value.Runtime;

        await created.Value.DisposeAsync();

        Assert.Equal(1, counting.Created);
        Assert.Equal(1, counting.Disposed);
        var after = await runtime.GetSessionAsync(Guid.NewGuid());
        Assert.True(after.IsError);
        Assert.Contains("disposed", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Different_cloud_subjects_get_isolated_scopes_and_repositories()
    {
        await using var provider = BuildCloudProvider();
        var factory = provider.GetRequiredService<IDysonSessionRuntimeScopeFactory>();
        var subjectA = Guid.NewGuid().ToString("D");
        var subjectB = Guid.NewGuid().ToString("D");

        await using var leaseA = (await factory.CreateAsync(subjectA)).Value;
        await using var leaseB = (await factory.CreateAsync(subjectB)).Value;

        Assert.NotSame(leaseA.Runtime, leaseB.Runtime);
        Assert.NotSame(leaseA.Runtime.Sessions, leaseB.Runtime.Sessions);
        Assert.Equal(subjectA, ((SubjectRecordingSessionRepository)leaseA.Runtime.Sessions).SubjectId);
        Assert.Equal(subjectB, ((SubjectRecordingSessionRepository)leaseB.Runtime.Sessions).SubjectId);

        leaseA.Runtime.ReportError("a-only");
        Assert.Equal("a-only", leaseA.Runtime.LastError);
        Assert.Null(leaseB.Runtime.LastError);
    }

    [Fact]
    public async Task Registry_retains_runtime_after_circuit_scope_dispose()
    {
        await using var provider = BuildLocalProvider();
        var registry = provider.GetRequiredService<DysonSessionRuntimeRegistry>();

        await using (var circuit = provider.CreateAsyncScope())
        {
            var first = await registry.GetOrCreateAsync(DysonSubjects.Local);
            Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
            Assert.Same(first.Value, circuit.ServiceProvider.GetRequiredService<DysonSessionRuntimeRegistry>()
                .TryGet(DysonSubjects.Local, out var fromCircuit)
                ? fromCircuit
                : null);

            var circuitChanges = new List<DysonRuntimeChange>();
            EventHandler<DysonRuntimeChange> circuitHandler = (_, change) => circuitChanges.Add(change);
            first.Value.Changed += circuitHandler;
            first.Value.ReportError("circuit-attached");
            Assert.Single(circuitChanges);
            first.Value.Changed -= circuitHandler;
        }

        Assert.True(registry.TryGet(DysonSubjects.Local, out var retained));
        Assert.NotNull(retained);
        var missing = await retained.GetSessionAsync(Guid.NewGuid());
        Assert.True(missing.IsError);
        Assert.Contains("not found", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disposed", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("circuit-attached", retained.LastError);

        retained.ReportError("after-circuit");
        Assert.Equal("after-circuit", retained.LastError);

        var second = await registry.GetOrCreateAsync(DysonSubjects.Local);
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.Same(retained, second.Value);
    }

    [Fact]
    public async Task Registry_dispose_disposes_ui_retained_scope()
    {
        await using var provider = BuildLocalProvider();
        var registry = provider.GetRequiredService<DysonSessionRuntimeRegistry>();
        var created = await registry.GetOrCreateAsync(DysonSubjects.Local);
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var runtime = created.Value;

        await registry.DisposeAsync();

        var after = await runtime.GetSessionAsync(Guid.NewGuid());
        Assert.True(after.IsError);
        Assert.Contains("disposed", after.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(registry.TryGet(DysonSubjects.Local, out _));
    }

    [Fact]
    public async Task Runtime_resolves_without_theme_or_browser_services()
    {
        var services = new ServiceCollection();
        services.AddDysonLocalHosting();
        RegisterRuntimeComposition(services);
        await using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<Harness.UI.Theme.ThemeService>());
        Assert.Null(provider.GetService<IDysonBrowserControl>());

        var factory = provider.GetRequiredService<IDysonSessionRuntimeScopeFactory>();
        await using var lease = (await factory.CreateAsync(DysonSubjects.Local)).Value;
        Assert.Equal(DysonSubjects.Local, lease.Runtime.SubjectId);
    }

    [Fact]
    public void Subject_bound_repository_stays_scoped()
    {
        var services = new ServiceCollection();
        services.AddDysonCloudHosting();
        RegisterRuntimeComposition(services);

        var descriptor = Assert.Single(
            services,
            d => d.ServiceType == typeof(IDysonSessionRepository));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, d => d.ServiceType == typeof(DysonSessionRuntimeRegistry)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, d => d.ServiceType == typeof(DysonSessionRuntime)).Lifetime);
    }

    private static ServiceProvider BuildLocalProvider()
    {
        var services = new ServiceCollection();
        services.AddDysonLocalHosting();
        RegisterRuntimeComposition(services);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildCloudProvider()
    {
        var services = new ServiceCollection();
        services.AddDysonCloudHosting();
        RegisterRuntimeComposition(services);
        return services.BuildServiceProvider();
    }

    private static void RegisterRuntimeComposition(IServiceCollection services)
    {
        services.AddScoped<IDysonSessionRepository, SubjectRecordingSessionRepository>();
        services.AddScoped<IDysonAgentSessionRuntimeFactory, UnusedSessionFactory>();
        services.AddScoped<DysonSessionRuntime>();
        services.AddSingleton<IDysonSessionRuntimeScopeFactory, DysonUiSessionRuntimeScopeFactory>();
        services.AddSingleton<DysonSessionRuntimeRegistry>();
    }

    private static CountingServiceScopeFactory WrapFactory(
        IServiceProvider provider,
        out DysonUiSessionRuntimeScopeFactory factory)
    {
        var counting = new CountingServiceScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        factory = new DysonUiSessionRuntimeScopeFactory(counting);
        return counting;
    }

    private sealed class CountingServiceScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created;
        public int Disposed;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Created);
            return new CountingScope(inner.CreateScope(), () => Interlocked.Increment(ref Disposed));
        }
    }

    private sealed class CountingScope(IServiceScope inner, Action onDisposed) : IServiceScope, IAsyncDisposable
    {
        private int _disposed;

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            inner.Dispose();
            onDisposed();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (inner is IAsyncDisposable async)
                await async.DisposeAsync().ConfigureAwait(false);
            else
                inner.Dispose();

            onDisposed();
        }
    }

    private sealed class UnusedSessionFactory : IDysonAgentSessionRuntimeFactory
    {
        public Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
            DysonAgentSessionRuntimeCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Result<DysonAgentSessionRuntimeLease, string>.AsError(
                    "Session factory is unused in scope factory tests."));
        }

        public Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            _ = sessionId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Result<DysonAgentSessionRuntimeLease, string>.AsError(
                    "Session factory is unused in scope factory tests."));
        }
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

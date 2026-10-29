using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// Plan 199 leftovers that do not need a live model or audio:
/// a replaced sidecar is resubscribed, voice-off drops only voice turns,
/// and a model push keeps going after the first failing sidecar.
/// </summary>
public class DysonVoiceSidecarMinorTests
{
    [Fact]
    public async Task Replaced_sidecar_is_resubscribed_and_the_old_one_is_not()
    {
        var prompts = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await Rig.CreateAsync(prompts);
        var meta = await harness.AdoptAsync(DysonAgentModes.MetaAgent);
        var first = await harness.CreateChildRowAsync(meta.PersistenceId);
        var second = await harness.CreateChildRowAsync(meta.PersistenceId);

        var ensured = await harness.Runtime.EnsureVoiceSidecarAsync(meta);
        Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
        Assert.Equal(first, harness.Runtime.VoiceSidecarSubscribedIdForTest(meta.PersistenceId));

        var deleted = await harness.Runtime.DeleteSessionAsync(first);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);

        var replaced = await harness.Runtime.EnsureVoiceSidecarAsync(meta);
        Assert.True(replaced.IsSuccess, replaced.IsError ? replaced.Error : null);
        Assert.Equal(second, harness.Runtime.VoiceSidecarSubscribedIdForTest(meta.PersistenceId));
        Assert.NotEqual(first, harness.Runtime.VoiceSidecarSubscribedIdForTest(meta.PersistenceId));

        var published = harness.Bus.Publish(
            DysonBusScopes.Session(second),
            new DysonVoiceSidecarMessageEvent(
                meta.PersistenceId,
                second,
                DysonVoiceSidecarDirection.ToSidecar,
                "hello replacement"));
        Assert.True(published.IsSuccess, published.IsError ? published.Error : null);

        var ran = await prompts.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("hello replacement", ran, StringComparison.Ordinal);

        // The old subscription is gone, so a publish on the old id never reaches a live session.
        harness.Bus.Publish(
            DysonBusScopes.Session(first),
            new DysonVoiceSidecarMessageEvent(
                meta.PersistenceId,
                first,
                DysonVoiceSidecarDirection.ToSidecar,
                "stale"));
        Assert.False(prompts.Task.IsCompleted && prompts.Task.Result.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Voice_off_drops_sidecar_traffic_and_keeps_a_typed_meta_prompt()
    {
        await using var harness = await Rig.CreateAsync(null);
        var meta = await harness.AdoptAsync(DysonAgentModes.MetaAgent);
        var sidecar = await harness.AdoptAsync(DysonAgentModes.VoiceSidecar);
        var work = await harness.AdoptAsync(DysonAgentModes.Work);

        Assert.True(harness.Runtime.EnqueuePrompt(
            meta.PersistenceId, DysonAgentSession.CreateNormalTurn("typed")).IsSuccess);
        Assert.True(harness.Runtime.EnqueuePrompt(
            meta.PersistenceId,
            DysonAgentSession.CreateNormalTurn(DysonInjectedUserComments.FormatSidecarMessage("from sidecar"))).IsSuccess);
        Assert.True(harness.Runtime.EnqueuePrompt(
            sidecar.PersistenceId,
            DysonAgentSession.CreateNormalTurn("[voice input] open the plan")).IsSuccess);
        Assert.True(harness.Runtime.EnqueuePrompt(
            sidecar.PersistenceId,
            DysonAgentSession.CreateNormalTurn(DysonInjectedUserComments.FormatMetaMessage("from meta"))).IsSuccess);
        Assert.True(harness.Runtime.EnqueuePrompt(
            work.PersistenceId, DysonAgentSession.CreateNormalTurn("unrelated")).IsSuccess);

        var dropped = harness.Runtime.DropVoiceTurnsOnVoiceOff();
        Assert.True(dropped.IsSuccess, dropped.IsError ? dropped.Error : null);

        Assert.Equal(
            ["typed"],
            harness.Runtime.GetQueuedPrompts(meta.PersistenceId).Select(p => p.Turn.Instruction));
        Assert.Empty(harness.Runtime.GetQueuedPrompts(sidecar.PersistenceId));
        Assert.Equal(
            ["unrelated"],
            harness.Runtime.GetQueuedPrompts(work.PersistenceId).Select(p => p.Turn.Instruction));
    }

    [Fact]
    public async Task ApplyVoiceSidecarModel_keeps_going_after_the_first_failure()
    {
        await using var harness = await Rig.CreateAsync(null);
        var first = await harness.AdoptAsync(DysonAgentModes.MetaAgent);
        var second = await harness.AdoptAsync(DysonAgentModes.MetaAgent);
        first.VoiceSidecar = await harness.AdoptAsync(DysonAgentModes.VoiceSidecar);
        second.VoiceSidecar = await harness.AdoptAsync(DysonAgentModes.VoiceSidecar);
        var refused = new OpenAiCompatibleAgentProvider(null, null);
        first.VoiceSidecar.Config.VoiceSidecarDefaultProvider = refused;
        second.VoiceSidecar.Config.VoiceSidecarDefaultProvider = refused;

        var applied = await harness.Runtime.ApplyVoiceSidecarModelAsync();
        Assert.True(applied.IsError);
        Assert.Equal(2, applied.Error.Split('\n').Length);
        Assert.Contains("Start a new session to switch provider kind", applied.Error, StringComparison.Ordinal);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Rig(SqliteConnection connection, DysonSessionRuntime runtime, DysonMessageBus bus)
        {
            _connection = connection;
            Runtime = runtime;
            Bus = bus;
        }

        public DysonSessionRuntime Runtime { get; }
        public DysonMessageBus Bus { get; }

        public static Task<Rig> CreateAsync(TaskCompletionSource<string>? prompts)
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subjectId = Guid.NewGuid().ToString("D");
            var subject = new DysonTempDb.MutableSubjectContext(subjectId);
            var sessions = DysonTempDb.Sessions(accessor, subject);
            var bus = new DysonMessageBus();
            var runtime = new DysonSessionRuntime(
                subject, sessions, new LoadingFactory(sessions, bus, prompts), voice: null);
            return Task.FromResult(new Rig(connection, runtime, bus));
        }

        public async Task<StubSession> AdoptAsync(string mode)
        {
            var created = await CreateRowAsync(mode, parentId: null);
            var session = new StubSession(mode, new DysonAgentSessionConfig { Bus = Bus }, null);
            session.SetPersistenceIdForTest(created);
            var adopted = Runtime.AdoptLiveSession(session);
            Assert.True(adopted.IsSuccess, adopted.IsError ? adopted.Error : null);
            Assert.Same(session, adopted.Value);
            return session;
        }

        public Task<Guid> CreateChildRowAsync(Guid parentId) =>
            CreateRowAsync(DysonAgentModes.VoiceSidecar, parentId);

        private async Task<Guid> CreateRowAsync(string mode, Guid? parentId)
        {
            var created = await Runtime.Sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                ParentSessionId = parentId,
                AgentMode = mode,
                Title = mode,
                SystemPromptSnapshot = mode,
            });
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            return created.Value;
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync().ConfigureAwait(false);
            Bus.Dispose();
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class LoadingFactory(
        IDysonSessionRepository sessions,
        DysonMessageBus bus,
        TaskCompletionSource<string>? prompts) : IDysonAgentSessionRuntimeFactory
    {
        public Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
            DysonAgentSessionRuntimeCreateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            var full = await sessions.GetFullSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (full.IsError)
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(full.Error);

            var session = new StubSession(
                full.Value.Session.AgentMode,
                new DysonAgentSessionConfig { Bus = bus },
                prompts);
            session.BindStore(sessions);
            session.RestoreForTest(full.Value);
            return Result<DysonAgentSessionRuntimeLease, string>.AsValue(
                new DysonAgentSessionRuntimeLease(session, static () => ValueTask.CompletedTask));
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(
        string mode,
        DysonAgentSessionConfig? config,
        TaskCompletionSource<string>? prompts) : DysonAgentSession(
        mode,
        config ?? new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void BindStore(IDysonSessionRepository store) => SessionStore = store;

        public void SetPersistenceIdForTest(Guid persistenceId) => SetPersistenceId(persistenceId);

        public void RestoreForTest(DysonPersistedSession state) => RestoreFromPersisted(state);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
        {
            prompts?.TrySetResult(turn.Instruction);
            return Task.FromResult(VoidResult<string>.Success);
        }

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

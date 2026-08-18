using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: runtime owns factory leases + PersistenceId graph; no UI focus.
/// </summary>
public class DysonSessionRuntimeTests
{
    [Fact]
    public async Task CreateRoot_registers_session_and_raises_graph_change()
    {
        await using var harness = await Harness.CreateAsync();

        DysonRuntimeChange? seen = null;
        harness.Runtime.Changed += (_, change) => seen = change;

        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        Assert.NotEqual(Guid.Empty, created.Value.PersistenceId);
        Assert.True(harness.Runtime.TryGetSession(created.Value.PersistenceId, out var found));
        Assert.Same(created.Value, found);
        Assert.Equal(1, harness.Factory.CreateCalls);
        Assert.NotNull(seen);
        Assert.Equal(DysonRuntimeChangeKind.SessionGraph, seen.Kind);
        Assert.Equal(created.Value.PersistenceId, seen.SessionId);
        Assert.Equal(harness.SubjectId, seen.SubjectId);
    }

    [Fact]
    public async Task LoadSession_reuses_live_instance_without_second_factory_call()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var loaded = await harness.Runtime.LoadSessionAsync(created.Value.PersistenceId);
        Assert.True(loaded.IsSuccess, loaded.IsError ? loaded.Error : null);
        Assert.Same(created.Value, loaded.Value);
        Assert.Equal(1, harness.Factory.CreateCalls);
        Assert.Equal(0, harness.Factory.LoadCalls);
    }

    [Fact]
    public async Task LoadSession_cold_loads_and_tracks_parent_id()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var rootId = created.Value.PersistenceId;

        var childRow = await harness.Sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            ParentSessionId = rootId,
            AgentMode = DysonAgentModes.Explore,
            Title = "child",
            SystemPromptSnapshot = "child",
        });
        Assert.True(childRow.IsSuccess, childRow.IsError ? childRow.Error : null);

        await harness.Runtime.DisposeAsync();
        await using var reloaded = new DysonSessionRuntime(
            new DysonTempDb.MutableSubjectContext(harness.SubjectId),
            harness.Sessions,
            harness.Factory);

        var loadedChild = await reloaded.LoadSessionAsync(childRow.Value);
        Assert.True(loadedChild.IsSuccess, loadedChild.IsError ? loadedChild.Error : null);
        Assert.Equal(1, harness.Factory.LoadCalls);
        Assert.True(reloaded.TryGetParentSessionId(childRow.Value, out var parentId));
        Assert.Equal(rootId, parentId);
        Assert.False(reloaded.TryGetSession(rootId, out _));
    }

    [Fact]
    public async Task Turn_log_todo_and_tool_events_persist()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var session = Assert.IsType<StubSession>(created.Value);
        var turn = DysonAgentSession.CreateNormalTurn("scan the tree");
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "grep-1",
            ToolName = "Grep",
            Stage = 0,
            ArgumentsJson = """{"pattern":"foo"}""",
        });
        session.AddTurnForTest(turn);
        turn.PrepareTrackedCalls();
        session.AppendLog("hello from runtime");
        var todo = await session.CreateTodoAsync("alpha", "One");
        Assert.True(todo.IsSuccess, todo.IsError ? todo.Error : null);

        await harness.Runtime.FlushPersistenceAsync();

        var full = await harness.Sessions.GetFullSessionAsync(session.PersistenceId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        var persisted = Assert.Single(full.Value.Turns);
        Assert.Equal(turn.Id, persisted.Id);
        Assert.Equal("scan the tree", persisted.Instruction);
        Assert.Contains(
            full.Value.Logs,
            log => log.Kind == nameof(DysonSessionLogKind.TurnStarted));
        Assert.Contains(
            full.Value.Logs,
            log => log.Kind == nameof(DysonSessionLogKind.LogLine)
                && log.PayloadJson.Contains("hello from runtime", StringComparison.Ordinal));
        Assert.Contains(
            full.Value.Logs,
            log => log.Kind == nameof(DysonSessionLogKind.ToolCallQueued));
        Assert.Contains(full.Value.Todos, t => t.TaskCode == "alpha");
    }

    [Fact]
    public async Task Subject_B_cannot_load_or_see_subject_A_session()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;

        var subjectB = Guid.NewGuid().ToString("D");
        var sessionsB = DysonTempDb.Sessions(harness.Accessor, new DysonTempDb.MutableSubjectContext(subjectB));
        var factoryB = new RecordingSessionFactory(sessionsB);
        await using var runtimeB = new DysonSessionRuntime(
            new DysonTempDb.MutableSubjectContext(subjectB),
            sessionsB,
            factoryB);

        var loaded = await runtimeB.LoadSessionAsync(sessionId);
        Assert.True(loaded.IsError);
        Assert.Contains("not found", loaded.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(runtimeB.TryGetSession(sessionId, out _));
        Assert.True(harness.Runtime.TryGetSession(sessionId, out _));
    }

    [Fact]
    public async Task Delete_unregisters_disposes_lease_and_removes_row()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;

        var deleted = await harness.Runtime.DeleteSessionAsync(sessionId);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);
        Assert.Equal(1, harness.Factory.DisposeCalls);
        Assert.False(harness.Runtime.TryGetSession(sessionId, out _));

        var full = await harness.Sessions.GetFullSessionAsync(sessionId);
        Assert.True(full.IsError);
        Assert.Contains("not found", full.Error, StringComparison.OrdinalIgnoreCase);

        var reload = await harness.Runtime.LoadSessionAsync(sessionId);
        Assert.True(reload.IsError);
    }

    [Fact]
    public async Task Spawned_child_is_registered_after_persistence_id_assigned()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var parent = Assert.IsType<StubSession>(created.Value);

        var childId = Guid.NewGuid();
        var child = parent.SpawnChildForTest(childId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && !harness.Runtime.TryGetSession(childId, out _))
            await Task.Delay(25);

        Assert.True(harness.Runtime.TryGetSession(childId, out var mapped));
        Assert.Same(child, mapped);
        Assert.True(harness.Runtime.TryGetParentSessionId(childId, out var parentId));
        Assert.Equal(parent.PersistenceId, parentId);

        var ran = false;
        var prompted = await harness.Runtime.ExecutePromptAsync(
            child,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(VoidResult<string>.Success);
            });
        Assert.True(prompted.IsSuccess, prompted.IsError ? prompted.Error : null);
        Assert.True(ran);
    }

    [Fact]
    public async Task Delete_parent_does_not_rematerialize_child_spawned_before_persistence_id()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var parent = Assert.IsType<StubSession>(created.Value);
        var parentId = parent.PersistenceId;

        var child = parent.SpawnUnpersistedChildForTest();
        var deleted = await harness.Runtime.DeleteSessionAsync(parentId);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);

        var childId = Guid.NewGuid();
        child.SetPersistenceIdForTest(childId);

        // Longer than one poller tick; the late PersistenceId must not rematerialize the child.
        await Task.Delay(100);

        Assert.False(harness.Runtime.TryGetSession(parentId, out _));
        Assert.False(harness.Runtime.TryGetSession(childId, out _));
        Assert.False(harness.Runtime.TryGetParentSessionId(childId, out _));

        var prompted = await harness.Runtime.ExecutePromptAsync(
            child,
            (_, _) => Task.FromResult(VoidResult<string>.Success));
        Assert.True(prompted.IsError);
        Assert.Contains("not registered", prompted.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_releases_leases()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        await harness.Runtime.DisposeAsync();
        await harness.Runtime.DisposeAsync();

        Assert.Equal(1, harness.Factory.DisposeCalls);
        Assert.False(harness.Runtime.TryGetSession(created.Value.PersistenceId, out _));
        var after = await harness.Runtime.GetSessionAsync(created.Value.PersistenceId);
        Assert.True(after.IsError);
        Assert.Contains("disposed", after.Error, StringComparison.OrdinalIgnoreCase);

        var recreate = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(recreate.IsError);
        Assert.Contains("disposed", recreate.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutePrompt_serializes_concurrent_calls_on_same_session()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var session = created.Value;

        var inFlight = 0;
        var maxInFlight = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<VoidResult<string>> Run(DysonAgentSession _, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref inFlight);
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref maxInFlight);
                if (current <= snapshot)
                    break;
            } while (Interlocked.CompareExchange(ref maxInFlight, current, snapshot) != snapshot);

            firstEntered.TrySetResult();
            try
            {
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }

        var first = harness.Runtime.ExecutePromptAsync(session, Run);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = harness.Runtime.ExecutePromptAsync(session, Run);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.IsError ? result.Error : null));
        Assert.Equal(1, maxInFlight);
        Assert.False(harness.Runtime.IsBusy(session.PersistenceId));
    }

    [Fact]
    public async Task CancelPrompt_cancels_in_flight_execution()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var session = created.Value;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busySeen = false;
        harness.Runtime.Changed += (_, change) =>
        {
            if (change.Kind == DysonRuntimeChangeKind.Busy && change.SessionId == session.PersistenceId)
                busySeen = true;
        };

        var execution = harness.Runtime.ExecutePromptAsync(
            session,
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(harness.Runtime.IsBusy(session.PersistenceId));

        var cancelled = harness.Runtime.CancelPrompt(session.PersistenceId);
        Assert.True(cancelled.IsSuccess, cancelled.IsError ? cancelled.Error : null);

        var result = await execution;
        Assert.True(result.IsError);
        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Runtime.IsBusy(session.PersistenceId));
        Assert.True(busySeen);
    }

    [Fact]
    public async Task Dispose_cancels_and_awaits_in_flight_prompt_before_releasing_lease()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var session = created.Value;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = harness.Runtime.ExecutePromptAsync(
            session,
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return VoidResult<string>.Success;
                }
                catch (OperationCanceledException)
                {
                    observedCancel.TrySetResult();
                    return VoidResult<string>.AsError("Prompt was cancelled.");
                }
            });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, harness.Factory.DisposeCalls);

        await harness.Runtime.DisposeAsync();

        Assert.True(observedCancel.Task.IsCompleted);
        var result = await execution;
        Assert.True(result.IsError);
        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.Factory.DisposeCalls);
        Assert.False(harness.Runtime.IsBusy(session.PersistenceId));
    }

    [Fact]
    public async Task ExecutePrompt_persists_unfinished_turns_after_success()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var session = Assert.IsType<StubSession>(created.Value);

        var result = await harness.Runtime.ExecutePromptAsync(
            session,
            (live, _) =>
            {
                var turn = DysonAgentSession.CreateNormalTurn("persist me");
                turn.AssistantText = "done";
                Assert.IsType<StubSession>(live).AddTurnForTest(turn);
                return Task.FromResult(VoidResult<string>.Success);
            });

        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        var liveTurn = Assert.Single(session.Turns);
        Assert.NotNull(liveTurn.CompletedUtc);

        await harness.Runtime.FlushPersistenceAsync();

        var full = await harness.Sessions.GetFullSessionAsync(session.PersistenceId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        var persisted = Assert.Single(full.Value.Turns);
        Assert.Equal(liveTurn.Id, persisted.Id);
        Assert.Equal("persist me", persisted.Instruction);
        Assert.Equal("done", persisted.AssistantText);
        Assert.NotNull(persisted.CompletedUtc);
        Assert.Contains(
            full.Value.Logs,
            log => log.Kind == nameof(DysonSessionLogKind.AgentReply)
                && log.PayloadJson.Contains("done", StringComparison.Ordinal));
        Assert.Contains(
            full.Value.Logs,
            log => log.Kind == nameof(DysonSessionLogKind.TurnCompleted));
    }

    [Fact]
    public async Task ExecutePrompt_full_summarize_drops_and_persists_earlier_turns()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var session = Assert.IsType<StubSession>(created.Value);

        var earlier = DysonAgentSession.CreateNormalTurn("old work");
        earlier.AssistantText = "facts";
        var first = await harness.Runtime.ExecutePromptAsync(
            session,
            (live, _) =>
            {
                Assert.IsType<StubSession>(live).AddTurnForTest(earlier);
                return Task.FromResult(VoidResult<string>.Success);
            });
        Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
        Assert.NotNull(earlier.CompletedUtc);

        var summary = DysonFullSummarizeFlow.CreateTurn();
        summary.AssistantText = new string('x', DysonFullSummarizeFlow.MaxSummaryCharacters + 10);
        var second = await harness.Runtime.ExecutePromptAsync(
            session,
            (live, _) =>
            {
                Assert.IsType<StubSession>(live).AddTurnForTest(summary);
                return Task.FromResult(VoidResult<string>.Success);
            });
        Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
        Assert.True(earlier.IsExcludedFromContext);
        Assert.False(summary.IsExcludedFromContext);
        Assert.Equal(DysonFullSummarizeFlow.MaxSummaryCharacters, summary.AssistantText!.Length);
        Assert.NotNull(summary.CompletedUtc);

        await harness.Runtime.FlushPersistenceAsync();

        var full = await harness.Sessions.GetFullSessionAsync(session.PersistenceId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        var persistedEarlier = Assert.Single(full.Value.Turns, t => t.Id == earlier.Id);
        Assert.True(persistedEarlier.IsExcludedFromContext);
        var persistedSummary = Assert.Single(full.Value.Turns, t => t.Id == summary.Id);
        Assert.False(persistedSummary.IsExcludedFromContext);
        Assert.Equal(DysonAgentTurnKind.FullSummarize, persistedSummary.Kind);
        Assert.Equal(DysonFullSummarizeFlow.MaxSummaryCharacters, persistedSummary.AssistantText!.Length);
    }

    [Fact]
    public async Task Prompt_queue_is_fifo_and_preserves_turn_identity()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;
        var first = DysonAgentSession.CreateNormalTurn("first");
        var second = DysonAgentSession.CreateNormalTurn("second");
        var queueChanges = new List<DysonRuntimeChange>();
        harness.Runtime.Changed += (_, change) =>
        {
            if (change.Kind == DysonRuntimeChangeKind.Queue)
                queueChanges.Add(change);
        };

        var missing = harness.Runtime.EnqueuePrompt(Guid.NewGuid(), first);
        Assert.True(missing.IsError);
        Assert.Contains("not registered", missing.Error, StringComparison.OrdinalIgnoreCase);

        var enqueuedFirst = harness.Runtime.EnqueuePrompt(sessionId, first);
        var enqueuedSecond = harness.Runtime.EnqueuePrompt(sessionId, second);
        Assert.True(enqueuedFirst.IsSuccess, enqueuedFirst.IsError ? enqueuedFirst.Error : null);
        Assert.True(enqueuedSecond.IsSuccess, enqueuedSecond.IsError ? enqueuedSecond.Error : null);
        Assert.Equal(2, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.True(harness.Runtime.TryPeekPrompt(sessionId, out var peeked));
        Assert.Same(first, peeked.Turn);
        Assert.Same(enqueuedFirst.Value, peeked);

        Assert.True(harness.Runtime.TryDequeuePrompt(sessionId, out var dequeuedFirst));
        Assert.Same(first, dequeuedFirst.Turn);
        Assert.Same(enqueuedFirst.Value, dequeuedFirst);
        Assert.True(harness.Runtime.TryDequeuePrompt(sessionId, out var dequeuedSecond));
        Assert.Same(second, dequeuedSecond.Turn);
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.False(harness.Runtime.TryDequeuePrompt(sessionId, out _));
        Assert.False(harness.Runtime.TryPeekPrompt(sessionId, out _));
        Assert.Equal(4, queueChanges.Count);
        Assert.All(queueChanges, change => Assert.Equal(sessionId, change.SessionId));
    }

    [Fact]
    public async Task EnqueuePrompt_rejects_task_end_reflect_and_does_not_queue()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;

        var enqueued = harness.Runtime.EnqueuePrompt(
            sessionId,
            DysonTaskLifecycleFlow.CreateTaskEndReflectTurn());
        Assert.True(enqueued.IsError);
        Assert.Contains("TaskEndReflect", enqueued.Error, StringComparison.Ordinal);
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(sessionId));
    }

    [Fact]
    public async Task Prompt_queue_copies_file_paths_immutably()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;
        var turn = DysonAgentSession.CreateNormalTurn("with files");
        var paths = new List<string> { "a.txt", "b.txt" };

        var enqueued = harness.Runtime.EnqueuePrompt(sessionId, turn, paths);
        Assert.True(enqueued.IsSuccess, enqueued.IsError ? enqueued.Error : null);
        paths[0] = "mutated.txt";
        paths.Add("c.txt");

        Assert.True(harness.Runtime.TryPeekPrompt(sessionId, out var peeked));
        Assert.Equal(["a.txt", "b.txt"], peeked.FilePaths);
        Assert.Equal(["a.txt", "b.txt"], enqueued.Value.FilePaths);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)peeked.FilePaths)[0] = "x.txt");
        Assert.Same(turn, peeked.Turn);
    }

    [Fact]
    public async Task Prompt_queue_clears_on_session_delete()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;
        var turn = DysonAgentSession.CreateNormalTurn("delete me");
        var queueSeen = false;
        harness.Runtime.Changed += (_, change) =>
        {
            if (change.Kind == DysonRuntimeChangeKind.Queue && change.SessionId == sessionId)
                queueSeen = true;
        };

        var enqueued = harness.Runtime.EnqueuePrompt(sessionId, turn);
        Assert.True(enqueued.IsSuccess, enqueued.IsError ? enqueued.Error : null);
        Assert.Equal(1, harness.Runtime.GetQueuedPromptCount(sessionId));

        var deleted = await harness.Runtime.DeleteSessionAsync(sessionId);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);
        Assert.True(queueSeen);
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.False(harness.Runtime.TryPeekPrompt(sessionId, out _));
        Assert.False(harness.Runtime.TryDequeuePrompt(sessionId, out _));
    }

    [Fact]
    public async Task Prompt_queue_clears_on_runtime_dispose()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;
        var turn = DysonAgentSession.CreateNormalTurn("dispose me");

        var enqueued = harness.Runtime.EnqueuePrompt(sessionId, turn);
        Assert.True(enqueued.IsSuccess, enqueued.IsError ? enqueued.Error : null);

        await harness.Runtime.DisposeAsync();

        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.False(harness.Runtime.TryPeekPrompt(sessionId, out _));
        Assert.False(harness.Runtime.TryDequeuePrompt(sessionId, out _));
        var after = harness.Runtime.EnqueuePrompt(sessionId, turn);
        Assert.True(after.IsError);
        Assert.Contains("disposed", after.Error, StringComparison.OrdinalIgnoreCase);
        var discarded = harness.Runtime.DiscardQueuedPrompts(sessionId);
        Assert.True(discarded.IsError);
        Assert.Contains("disposed", discarded.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prompt_queue_survives_circuit_detach_without_coupling()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        var sessionId = created.Value.PersistenceId;
        var turn = DysonAgentSession.CreateNormalTurn("stay queued");
        var circuitChanges = new List<DysonRuntimeChange>();
        EventHandler<DysonRuntimeChange> circuit = (_, change) => circuitChanges.Add(change);
        harness.Runtime.Changed += circuit;

        var enqueued = harness.Runtime.EnqueuePrompt(sessionId, turn, ["keep.txt"]);
        Assert.True(enqueued.IsSuccess, enqueued.IsError ? enqueued.Error : null);
        Assert.Contains(circuitChanges, change => change.Kind == DysonRuntimeChangeKind.Queue);

        harness.Runtime.Changed -= circuit;

        Assert.Equal(1, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.True(harness.Runtime.TryPeekPrompt(sessionId, out var peeked));
        Assert.Same(turn, peeked.Turn);
        Assert.Equal(["keep.txt"], peeked.FilePaths);
        Assert.False(harness.Runtime.IsBusy(sessionId));

        DysonRuntimeChange? afterReconnect = null;
        EventHandler<DysonRuntimeChange> reattached = (_, change) => afterReconnect = change;
        harness.Runtime.Changed += reattached;
        var discarded = harness.Runtime.DiscardQueuedPrompts(sessionId);
        harness.Runtime.Changed -= reattached;

        Assert.True(discarded.IsSuccess, discarded.IsError ? discarded.Error : null);
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.NotNull(afterReconnect);
        Assert.Equal(DysonRuntimeChangeKind.Queue, afterReconnect.Kind);
        Assert.Equal(sessionId, afterReconnect.SessionId);
        Assert.Equal(1, circuitChanges.Count(change => change.Kind == DysonRuntimeChangeKind.Queue));
    }

    [Fact]
    public async Task Factory_error_is_returned_as_result()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Factory.FailNextCreate = "provider missing";

        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsError);
        Assert.Equal("provider missing", created.Error);
        Assert.Equal("provider missing", harness.Runtime.LastError);
        Assert.Equal(1, harness.Factory.CreateCalls);
        Assert.Equal(0, harness.Factory.DisposeCalls);
    }

    [Fact]
    public async Task Registration_is_safe_against_duplicate_factory_lease()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.Runtime.CreateRootAsync(RootRequest());
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var duplicate = await harness.Factory.LoadAsync(created.Value.PersistenceId);
        Assert.True(duplicate.IsSuccess, duplicate.IsError ? duplicate.Error : null);
        Assert.NotSame(created.Value, duplicate.Value.Session);

        var adopted = await harness.Runtime.LoadSessionAsync(created.Value.PersistenceId);
        Assert.True(adopted.IsSuccess, adopted.IsError ? adopted.Error : null);
        Assert.Same(created.Value, adopted.Value);
        await duplicate.Value.DisposeAsync();
        Assert.Equal(1, harness.Factory.DisposeCalls);
    }

    private static DysonAgentSessionRuntimeCreateRequest RootRequest() =>
        new()
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = Guid.NewGuid(),
        };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(
            SqliteConnection connection,
            DysonDbAccessor accessor,
            IDysonSessionRepository sessions,
            RecordingSessionFactory factory,
            DysonSessionRuntime runtime,
            string subjectId)
        {
            _connection = connection;
            Accessor = accessor;
            Sessions = sessions;
            Factory = factory;
            Runtime = runtime;
            SubjectId = subjectId;
        }

        public DysonDbAccessor Accessor { get; }
        public IDysonSessionRepository Sessions { get; }
        public RecordingSessionFactory Factory { get; }
        public DysonSessionRuntime Runtime { get; }
        public string SubjectId { get; }

        public static Task<Harness> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subjectId = Guid.NewGuid().ToString("D");
            var sessions = DysonTempDb.Sessions(accessor, new DysonTempDb.MutableSubjectContext(subjectId));
            var factory = new RecordingSessionFactory(sessions);
            var runtime = new DysonSessionRuntime(
                new DysonTempDb.MutableSubjectContext(subjectId),
                sessions,
                factory);
            return Task.FromResult(new Harness(connection, accessor, sessions, factory, runtime, subjectId));
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RecordingSessionFactory(IDysonSessionRepository sessions) : IDysonAgentSessionRuntimeFactory
    {
        public int CreateCalls;
        public int LoadCalls;
        public int DisposeCalls;
        public string? FailNextCreate;

        public async Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
            DysonAgentSessionRuntimeCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CreateCalls);
            if (FailNextCreate is { } error)
            {
                FailNextCreate = null;
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(error);
            }

            var created = await sessions.CreateSessionAsync(
                    new DysonSessionCreateRequest
                    {
                        RuntimeId = 0,
                        AgentMode = request.AgentMode,
                        Title = "runtime-root",
                        SystemPromptSnapshot = "runtime-core",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(created.Error);

            var session = new StubSession();
            session.BindStore(sessions);
            session.SetPersistenceIdForTest(created.Value);
            return Result<DysonAgentSessionRuntimeLease, string>.AsValue(Wrap(session));
        }

        public async Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LoadCalls);
            var full = await sessions.GetFullSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (full.IsError)
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(full.Error);

            var session = new StubSession();
            session.BindStore(sessions);
            session.RestoreForTest(full.Value);
            return Result<DysonAgentSessionRuntimeLease, string>.AsValue(Wrap(session));
        }

        private DysonAgentSessionRuntimeLease Wrap(DysonAgentSession session) =>
            new(
                session,
                () =>
                {
                    Interlocked.Increment(ref DisposeCalls);
                    return ValueTask.CompletedTask;
                });
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void BindStore(IDysonSessionRepository store) => SessionStore = store;

        public void SetPersistenceIdForTest(Guid persistenceId) => SetPersistenceId(persistenceId);

        public void RestoreForTest(DysonPersistedSession state) => RestoreFromPersisted(state);

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public StubSession SpawnChildForTest(Guid persistenceId)
        {
            var child = SpawnUnpersistedChildForTest();
            child.SetPersistenceIdForTest(persistenceId);
            return child;
        }

        public StubSession SpawnUnpersistedChildForTest()
        {
            var child = new StubSession();
            RegisterSubagent(child);
            return child;
        }

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

using System.Reflection;
using System.Text.Json;
using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Theme;
using Microsoft.Data.Sqlite;
using Microsoft.JSInterop;

namespace Harness.Tests;

/// <summary>
/// ponytail: demo create/resume/prompt delegate to the retained runtime without duplicate persist.
/// </summary>
public class DysonUiHostRuntimeDelegationTests
{
    [Fact]
    public async Task Start_and_resume_reuse_retained_runtime_session()
    {
        await using var harness = await HostHarness.CreateAsync();
        await using var first = harness.CreateHost();

        var started = await first.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = first.Session ?? throw new InvalidOperationException("Expected focused session.");
        Assert.NotEqual(Guid.Empty, session.PersistenceId);
        Assert.True(harness.Runtime.TryGetSession(session.PersistenceId, out var retained));
        Assert.Same(session, retained);

        await first.DisposeAsync();

        await using var second = harness.CreateHost();
        var resumed = await second.ResumeSessionAsync(session.PersistenceId);
        Assert.True(resumed.IsSuccess, resumed.IsError ? resumed.Error : null);
        Assert.Same(session, second.Session);
        Assert.Equal(1, harness.SessionFactory.CreateCalls);
        Assert.Equal(0, harness.SessionFactory.LoadCalls);
    }

    [Fact]
    public async Task Dispose_during_runtime_prompt_does_not_cancel_and_second_host_reattaches()
    {
        await using var harness = await HostHarness.CreateAsync(waiting: true);
        var first = harness.CreateHost();

        var started = await first.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = first.Session ?? throw new InvalidOperationException("Expected focused session.");

        var prompt = first.PromptAsync("hold this");
        await harness.WaitingFactory!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.IsBusy);
        Assert.True(harness.Runtime.IsBusy(session.PersistenceId));

        await first.DisposeAsync();

        Assert.False(prompt.IsCompleted);
        Assert.True(harness.Runtime.IsBusy(session.PersistenceId));
        Assert.Equal(0, harness.WaitingFactory.CancelObserved);

        await using var second = harness.CreateHost();
        var resumed = await second.ResumeSessionAsync(session.PersistenceId);
        Assert.True(resumed.IsSuccess, resumed.IsError ? resumed.Error : null);
        Assert.Same(session, second.Session);
        Assert.True(second.IsBusy);

        harness.WaitingFactory.Release.TrySetResult();
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.Equal(0, harness.WaitingFactory.CancelObserved);
        Assert.DoesNotContain("cancelled", result.IsError ? result.Error : "", StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Runtime.IsBusy(session.PersistenceId));
    }

    [Fact]
    public async Task Dispose_during_runtime_prompt_keeps_pending_follow_up_for_second_host()
    {
        await using var harness = await HostHarness.CreateAsync(waiting: true);
        var first = harness.CreateHost();

        var started = await first.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = first.Session ?? throw new InvalidOperationException("Expected focused session.");

        harness.WaitingFactory!.EnqueueFollowUp = true;
        var prompt = first.PromptAsync("hold this");
        await harness.WaitingFactory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await first.DisposeAsync();
        await using var second = harness.CreateHost();
        var resumed = await second.ResumeSessionAsync(session.PersistenceId);
        Assert.True(resumed.IsSuccess, resumed.IsError ? resumed.Error : null);

        harness.WaitingFactory.Release.TrySetResult();
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
               && (!session.Turns.Any(turn =>
                       string.Equals(turn.Instruction, WaitingSession.FollowUpInstruction, StringComparison.Ordinal))
                   || session.HasPendingTurn
                   || harness.Runtime.IsBusy(session.PersistenceId)))
        {
            await Task.Delay(25);
        }

        Assert.Contains(
            session.Turns,
            turn => string.Equals(turn.Instruction, WaitingSession.FollowUpInstruction, StringComparison.Ordinal));
        Assert.False(session.HasPendingTurn);
        Assert.False(harness.Runtime.IsBusy(session.PersistenceId));
    }

    [Fact]
    public async Task Dispose_leaves_runtime_queue_and_second_host_drains_once()
    {
        await using var harness = await HostHarness.CreateAsync(waiting: true);
        var first = harness.CreateHost();

        var started = await first.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = first.Session ?? throw new InvalidOperationException("Expected focused session.");

        var prompt = first.PromptAsync("hold this");
        await harness.WaitingFactory!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.IsBusy);
        Assert.True(harness.Runtime.IsBusy(session.PersistenceId));

        const string queuedInstruction = "queued later";
        var queued = await first.PromptAsync(queuedInstruction);
        Assert.True(queued.IsSuccess, queued.IsError ? queued.Error : null);
        Assert.Equal(1, harness.Runtime.GetQueuedPromptCount(session.PersistenceId));
        Assert.True(harness.Runtime.TryPeekPrompt(session.PersistenceId, out var peeked));
        Assert.Equal(queuedInstruction, peeked.Turn.Instruction);
        Assert.Same(session, first.Session);

        var sessionId = session.PersistenceId;
        await first.DisposeAsync();
        Assert.Equal(1, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.True(harness.Runtime.TryGetSession(sessionId, out var retainedAfterDispose));
        Assert.Same(session, retainedAfterDispose);

        harness.WaitingFactory.Release.TrySetResult();
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.False(harness.Runtime.IsBusy(sessionId));
        Assert.Equal(1, harness.Runtime.GetQueuedPromptCount(sessionId));

        await using var second = harness.CreateHost();
        var resumed = await second.ResumeSessionAsync(sessionId);
        Assert.True(resumed.IsSuccess, resumed.IsError ? resumed.Error : null);
        Assert.Same(session, second.Session);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
               && session.Turns.Count(turn =>
                   string.Equals(turn.Instruction, queuedInstruction, StringComparison.Ordinal)) < 1)
        {
            await Task.Delay(25);
        }

        Assert.Equal(
            1,
            session.Turns.Count(turn =>
                string.Equals(turn.Instruction, queuedInstruction, StringComparison.Ordinal)));
        Assert.Same(session, second.Session);

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
               && (harness.Runtime.IsBusy(sessionId)
                   || harness.Runtime.GetQueuedPromptCount(sessionId) > 0
                   || session.HasPendingTurn))
        {
            await Task.Delay(25);
        }

        Assert.False(harness.Runtime.IsBusy(sessionId));
        Assert.Equal(
            1,
            session.Turns.Count(turn =>
                string.Equals(turn.Instruction, queuedInstruction, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Legacy_host_queue_does_not_survive_dispose()
    {
        await using var harness = await HostHarness.CreateAsync();
        var first = harness.CreateHost(attachRuntime: false);

        var started = await first.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = first.Session ?? throw new InvalidOperationException("Expected focused session.");
        Assert.NotEqual(Guid.Empty, session.PersistenceId);
        Assert.False(harness.Runtime.TryGetSession(session.PersistenceId, out _));

        const string queuedInstruction = "legacy queued";
        first.MarkSessionBusyForTests(session.PersistenceId);
        var queued = await first.PromptAsync(queuedInstruction);
        Assert.True(queued.IsSuccess, queued.IsError ? queued.Error : null);
        Assert.Single(first.QueuedPrompts);
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(session.PersistenceId));

        var sessionId = session.PersistenceId;
        await first.DisposeAsync();
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(sessionId));

        await using var second = harness.CreateHost(attachRuntime: false);
        var resumed = await second.ResumeSessionAsync(sessionId);
        Assert.True(resumed.IsSuccess, resumed.IsError ? resumed.Error : null);
        Assert.NotNull(second.Session);
        Assert.Empty(second.QueuedPrompts);
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(sessionId));
        Assert.DoesNotContain(
            second.Session.Turns,
            turn => string.Equals(turn.Instruction, queuedInstruction, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_runtime_session_unregisters_and_does_not_leave_live_instance()
    {
        await using var harness = await HostHarness.CreateAsync();
        await using var host = harness.CreateHost();

        var started = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = host.Session ?? throw new InvalidOperationException("Expected focused session.");
        var sessionId = session.PersistenceId;

        var deleted = await host.DeleteSessionAsync(sessionId);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);
        Assert.False(harness.Runtime.TryGetSession(sessionId, out _));

        var full = await harness.Sessions.GetFullSessionAsync(sessionId);
        Assert.True(full.IsError);
        Assert.Contains("not found", full.Error, StringComparison.OrdinalIgnoreCase);

        var reloaded = await harness.Runtime.LoadSessionAsync(sessionId);
        Assert.True(reloaded.IsError);
    }

    [Fact]
    public async Task Delete_from_new_host_routes_to_runtime_without_resume()
    {
        await using var harness = await HostHarness.CreateAsync();
        Guid sessionId;
        await using (var first = harness.CreateHost())
        {
            var started = await first.StartNewSessionAsync(
                DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
            Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
            var session = first.Session ?? throw new InvalidOperationException("Expected focused session.");
            sessionId = session.PersistenceId;
        }

        Assert.True(harness.Runtime.TryGetSession(sessionId, out _));

        await using var second = harness.CreateHost();
        var deleted = await second.DeleteSessionAsync(sessionId);
        Assert.True(deleted.IsSuccess, deleted.IsError ? deleted.Error : null);
        Assert.False(harness.Runtime.TryGetSession(sessionId, out _));

        var full = await harness.Sessions.GetFullSessionAsync(sessionId);
        Assert.True(full.IsError);
        Assert.Contains("not found", full.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_backed_prompt_does_not_duplicate_turn_logs()
    {
        await using var harness = await HostHarness.CreateAsync();
        await using var host = harness.CreateHost();

        var started = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = host.Session ?? throw new InvalidOperationException("Expected focused session.");

        var prompted = await host.PromptAsync("hello runtime");
        Assert.True(prompted.IsSuccess, prompted.IsError ? prompted.Error : null);
        await harness.Runtime.FlushPersistenceAsync();

        var full = await harness.Sessions.GetFullSessionAsync(session.PersistenceId);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        var turnCount = session.Turns.Count;
        Assert.True(turnCount > 0);
        Assert.Equal(turnCount, full.Value.Turns.Count);
        Assert.Equal(
            turnCount,
            full.Value.Logs.Count(log => log.Kind == nameof(DysonSessionLogKind.TurnStarted)));
        Assert.Equal(
            turnCount,
            full.Value.Logs.Count(log => log.Kind == nameof(DysonSessionLogKind.AgentReply)));
        Assert.Equal(
            turnCount,
            full.Value.Logs.Count(log => log.Kind == nameof(DysonSessionLogKind.TurnCompleted)));
    }

    [Fact]
    public async Task Dispose_unhooks_ui_handlers_and_leaves_runtime_session_alive()
    {
        await using var harness = await HostHarness.CreateAsync();
        var host = harness.CreateHost();

        var started = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = host.Session ?? throw new InvalidOperationException("Expected focused session.");

        var notifies = 0;
        host.Changed += () => Interlocked.Increment(ref notifies);

        await host.DisposeAsync();
        var afterDispose = Volatile.Read(ref notifies);

        Assert.True(harness.Runtime.TryGetSession(session.PersistenceId, out var retained));
        Assert.Same(session, retained);
        Assert.DoesNotContain(host, EventTargets(session, nameof(DysonAgentSession.TurnAdded)));
        Assert.DoesNotContain(host, EventTargets(session, nameof(DysonAgentSession.LogAppended)));
        Assert.DoesNotContain(host, EventTargets(session, nameof(DysonAgentSession.TodosChanged)));

        session.AppendLog("after host dispose");
        var todo = await session.CreateTodoAsync("post-dispose", "must not notify disposed host");
        Assert.True(todo.IsSuccess, todo.IsError ? todo.Error : null);
        Assert.Equal(afterDispose, Volatile.Read(ref notifies));
    }

    [Fact]
    public async Task Focus_switch_does_not_cancel_runtime_prompt()
    {
        await using var harness = await HostHarness.CreateAsync(waiting: true);
        await using var host = harness.CreateHost();

        var startedA = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedA.IsSuccess, startedA.IsError ? startedA.Error : null);
        var sessionA = host.Session ?? throw new InvalidOperationException("Expected session A.");
        var sessionAId = sessionA.PersistenceId;

        var prompt = host.PromptAsync("hold this");
        await harness.WaitingFactory!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.IsBusy);
        Assert.True(host.IsSessionBusy(sessionAId));
        Assert.True(harness.Runtime.IsBusy(sessionAId));

        var startedB = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedB.IsSuccess, startedB.IsError ? startedB.Error : null);
        Assert.NotEqual(sessionAId, host.ActiveSessionId);
        Assert.False(host.IsBusy);
        Assert.True(host.IsSessionBusy(sessionAId));
        Assert.True(harness.Runtime.IsBusy(sessionAId));
        Assert.Equal(0, harness.WaitingFactory.CancelObserved);

        harness.WaitingFactory.Release.TrySetResult();
        var result = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.Equal(0, harness.WaitingFactory.CancelObserved);
        Assert.False(harness.Runtime.IsBusy(sessionAId));
        Assert.False(host.IsSessionBusy(sessionAId));
    }

    [Fact]
    public async Task HasActiveSubagents_resolves_unfocused_idle_parent_with_live_child()
    {
        await using var harness = await HostHarness.CreateAsync();
        var host = harness.CreateHost();

        var startedA = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedA.IsSuccess, startedA.IsError ? startedA.Error : null);
        var sessionA = host.Session ?? throw new InvalidOperationException("Expected session A.");
        var sessionAId = sessionA.PersistenceId;

        var spawned = await sessionA.CreateChildAsync(DysonAgentModes.Explore, "hold");
        Assert.True(spawned.IsSuccess, spawned.IsError ? spawned.Error : null);
        Assert.True(sessionA.TryGetSubagent(spawned.Value.SubagentId, out var child));
        CancelBackgroundRunForTests(child);
        Assert.Equal(DysonSessionStatus.Active, child.Status);

        Assert.False(host.IsSessionBusy(sessionAId));
        Assert.True(host.HasActiveSubagents(sessionAId));
        Assert.True(host.HasActiveSubagents());

        var startedB = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedB.IsSuccess, startedB.IsError ? startedB.Error : null);
        Assert.NotEqual(sessionAId, host.ActiveSessionId);
        Assert.True(host.HasActiveSubagents(sessionAId));
        Assert.False(host.IsSessionBusy(sessionAId));
        Assert.False(host.HasActiveSubagents());

        await host.DisposeAsync();
        await using var second = harness.CreateHost();
        var startedOnSecond = await second.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedOnSecond.IsSuccess, startedOnSecond.IsError ? startedOnSecond.Error : null);
        Assert.NotEqual(sessionAId, second.ActiveSessionId);
        Assert.True(second.HasActiveSubagents(sessionAId));
        Assert.False(second.IsSessionBusy(sessionAId));

        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Completed, "done"));
        Assert.False(second.HasActiveSubagents(sessionAId));
    }

    [Fact]
    public async Task StopAllExecution_with_live_child_does_not_restart()
    {
        await using var harness = await HostHarness.CreateAsync(waiting: true);
        await using var host = harness.CreateHost();

        var started = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = host.Session ?? throw new InvalidOperationException("Expected focused session.");
        var parentId = session.PersistenceId;

        var prompt = host.PromptAsync("hold this");
        await harness.WaitingFactory!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.IsSessionBusy(parentId));

        var spawned = await session.CreateChildAsync(DysonAgentModes.Explore, "hold child");
        Assert.True(spawned.IsSuccess, spawned.IsError ? spawned.Error : null);
        Assert.True(session.TryGetSubagent(spawned.Value.SubagentId, out var child));
        await harness.WaitingFactory.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => child.HasActiveBackgroundRun && child.PersistenceId != Guid.Empty,
            TimeSpan.FromSeconds(5));

        await host.StopAllExecution();

        await WaitUntilAsync(
            () => child.Status == DysonSessionStatus.Stopped
                  && !child.HasActiveBackgroundRun
                  && !host.IsSessionBusy(parentId),
            TimeSpan.FromSeconds(5));

        var promptResult = await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(promptResult.IsError);
        Assert.Contains("cancel", promptResult.Error, StringComparison.OrdinalIgnoreCase);

        await Task.Delay(300);

        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.False(child.HasActiveBackgroundRun);
        Assert.False(host.IsSessionBusy(parentId));
        Assert.False(host.IsBusy);
        Assert.Equal(0, harness.WaitingFactory.ReportProcessingCalls);
        Assert.DoesNotContain(
            session.Turns,
            turn => turn.Kind == DysonAgentTurnKind.SubagentReportProcessing);
        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(parentId));

        var persisted = await harness.Sessions.GetFullSessionAsync(child.PersistenceId);
        Assert.True(persisted.IsSuccess, persisted.IsError ? persisted.Error : null);
        Assert.Equal(DysonSessionStatus.Stopped, persisted.Value.Session.Status);
    }

    [Fact]
    public async Task StopAllExecution_queued_only_discards_without_starting()
    {
        await using var harness = await HostHarness.CreateAsync();
        await using var host = harness.CreateHost();

        var started = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = host.Session ?? throw new InvalidOperationException("Expected focused session.");

        const string queuedInstruction = "queued later";
        var enqueued = harness.Runtime.EnqueuePrompt(
            session.PersistenceId,
            DysonAgentSession.CreateNormalTurn(queuedInstruction));
        Assert.True(enqueued.IsSuccess, enqueued.IsError ? enqueued.Error : null);

        Assert.False(host.IsBusy);
        Assert.True(host.CanStopExecution());
        Assert.Equal(1, harness.Runtime.GetQueuedPromptCount(session.PersistenceId));

        await host.StopAllExecution();

        Assert.Equal(0, harness.Runtime.GetQueuedPromptCount(session.PersistenceId));
        Assert.False(host.CanStopExecution());
        Assert.False(host.IsBusy);
        Assert.DoesNotContain(
            session.Turns,
            turn => string.Equals(turn.Instruction, queuedInstruction, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsSessionBusy_for_focused_child_with_background_run()
    {
        await using var harness = await HostHarness.CreateAsync(waiting: true);
        await using var host = harness.CreateHost();

        var started = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var session = host.Session ?? throw new InvalidOperationException("Expected focused session.");
        var parentId = session.PersistenceId;

        var spawned = await session.CreateChildAsync(DysonAgentModes.Explore, "hold child");
        Assert.True(spawned.IsSuccess, spawned.IsError ? spawned.Error : null);
        Assert.True(session.TryGetSubagent(spawned.Value.SubagentId, out var child));
        await harness.WaitingFactory!.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => child.HasActiveBackgroundRun && child.PersistenceId != Guid.Empty,
            TimeSpan.FromSeconds(5));

        var childId = child.PersistenceId;
        Assert.False(host.IsSessionBusy(parentId));
        Assert.True(host.HasActiveSubagents(parentId));
        Assert.True(host.IsSessionBusy(childId));

        var focused = await host.NavigateToSessionAsync(childId);
        Assert.True(focused.IsSuccess, focused.IsError ? focused.Error : null);
        Assert.Equal(childId, host.ActiveSessionId);
        Assert.True(host.IsBusy);
        Assert.True(host.IsSessionBusy(childId));
        Assert.False(host.IsSessionBusy(parentId));
        Assert.True(host.HasActiveSubagents(parentId));

        await host.StopAllExecution();
        await WaitUntilAsync(
            () => child.Status == DysonSessionStatus.Stopped && !child.HasActiveBackgroundRun,
            TimeSpan.FromSeconds(5));

        Assert.False(host.IsSessionBusy(childId));
        Assert.False(host.IsBusy);
        Assert.False(host.IsSessionBusy(parentId));
        Assert.False(host.HasActiveSubagents(parentId));
    }

    [Fact]
    public async Task GetSubagentCardState_updates_latest_step_and_child_text_raises_changed()
    {
        await using var harness = await HostHarness.CreateAsync(waiting: true);
        await using var host = harness.CreateHost();

        var started = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(started.IsSuccess, started.IsError ? started.Error : null);
        var parent = host.Session ?? throw new InvalidOperationException("Expected focused session.");

        var spawned = await parent.CreateChildAsync(DysonAgentModes.Explore, "hold child");
        Assert.True(spawned.IsSuccess, spawned.IsError ? spawned.Error : null);
        Assert.True(parent.TryGetSubagent(spawned.Value.SubagentId, out var child));
        await harness.WaitingFactory!.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => child.PersistenceId != Guid.Empty
                  && host.GetSubagentCardState(child.PersistenceId) is not null,
            TimeSpan.FromSeconds(5));
        CancelBackgroundRunForTests(child);

        var empty = host.GetSubagentCardState(child.PersistenceId);
        Assert.NotNull(empty);
        Assert.Null(empty.LatestTurnStepTitle);

        var turn = child.AppendDisplayInfoTurn("placeholder");
        var afterTurn = host.GetSubagentCardState(child.PersistenceId);
        Assert.NotNull(afterTurn);
        Assert.Null(afterTurn.LatestTurnStepTitle);

        var notifies = 0;
        host.Changed += () => Interlocked.Increment(ref notifies);
        var before = Volatile.Read(ref notifies);

        turn.AppendReasoningRound(
            0,
            "# Loading required topic files\n\nbody",
            interimText: null,
            includeInterimText: false);

        Assert.True(Volatile.Read(ref notifies) > before);
        var afterReasoning = host.GetSubagentCardState(child.PersistenceId);
        Assert.NotNull(afterReasoning);
        Assert.Equal("Loading required topic files", afterReasoning.LatestTurnStepTitle);

        before = Volatile.Read(ref notifies);
        turn.AppendReasoningDelta("private live reasoning");
        await WaitUntilAsync(
            () => Volatile.Read(ref notifies) > before,
            TimeSpan.FromSeconds(2));
        var whileStreaming = host.GetSubagentCardState(child.PersistenceId);
        Assert.NotNull(whileStreaming);
        Assert.Equal("Thinking 2", whileStreaming.LatestTurnStepTitle);

        turn.AgentTitle = "Report accepted";
        var afterTitle = host.GetSubagentCardState(child.PersistenceId);
        Assert.NotNull(afterTitle);
        Assert.Equal("Report accepted", afterTitle.LatestTurnStepTitle);
    }

    [Fact]
    public async Task Ask_ui_waits_until_session_is_rejoined()
    {
        await using var harness = await HostHarness.CreateAsync();
        await using var host = harness.CreateHost();

        var startedA = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedA.IsSuccess, startedA.IsError ? startedA.Error : null);
        var sessionA = host.Session ?? throw new InvalidOperationException("Expected session A.");

        var askTask = sessionA.AskQuestionAsync(
            """{"questions":[{"prompt":"Name?","options":["Ada","Grace"]}]}""",
            CancellationToken.None);
        await WaitUntilAsync(() => host.PendingAskUi is not null, TimeSpan.FromSeconds(2));
        Assert.NotNull(host.PendingAskUi);
        Assert.Equal(sessionA.PersistenceId, host.PendingAskUi.SessionPersistenceId);
        Assert.NotNull(sessionA.PendingAskQuestions);

        var startedB = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedB.IsSuccess, startedB.IsError ? startedB.Error : null);
        Assert.Null(host.PendingAskUi);
        Assert.NotNull(sessionA.PendingAskQuestions);
        Assert.False(askTask.IsCompleted);

        RaiseParentEventsChanged(sessionA);
        InvokeMaybeOpenAskUiForEvent(host, sessionA);
        Assert.Null(host.PendingAskUi);
        Assert.NotNull(sessionA.PendingAskQuestions);

        var resumed = await host.ResumeSessionAsync(sessionA.PersistenceId);
        Assert.True(resumed.IsSuccess, resumed.IsError ? resumed.Error : null);
        Assert.NotNull(host.PendingAskUi);
        Assert.Equal(sessionA.PersistenceId, host.PendingAskUi.SessionPersistenceId);
        Assert.Equal(DysonAskUiSource.RootAskQuestion, host.PendingAskUi.Source);

        var respond = sessionA.RespondToAskQuestion("A1 - Ada");
        Assert.True(respond.IsSuccess, respond.IsError ? respond.Error : null);
        var asked = await askTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(asked.IsSuccess, asked.IsError ? asked.Error : null);
    }

    [Fact]
    public async Task User_dialog_ui_waits_until_session_is_rejoined()
    {
        await using var harness = await HostHarness.CreateAsync();
        await using var host = harness.CreateHost();

        var startedA = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedA.IsSuccess, startedA.IsError ? startedA.Error : null);
        var sessionA = host.Session ?? throw new InvalidOperationException("Expected session A.");

        var dialogTask = sessionA.PromptUserDialogAsync(
            """
            {
              "title": "Ship?",
              "description": "Ready to publish?",
              "actions": [{ "label": "Publish", "primary": true }, { "label": "Hold" }]
            }
            """,
            CancellationToken.None);
        await WaitUntilAsync(() => host.PendingUserDialogUi is not null, TimeSpan.FromSeconds(2));
        Assert.NotNull(host.PendingUserDialogUi);
        Assert.Equal(sessionA.PersistenceId, host.PendingUserDialogUi.SessionPersistenceId);
        Assert.NotNull(sessionA.PendingUserDialog);

        var startedB = await host.StartNewSessionAsync(
            DysonAgentModes.Work, harness.SlugId, harness.WorkDirectoryId);
        Assert.True(startedB.IsSuccess, startedB.IsError ? startedB.Error : null);
        Assert.Null(host.PendingUserDialogUi);
        Assert.NotNull(sessionA.PendingUserDialog);
        Assert.False(dialogTask.IsCompleted);

        RaiseParentEventsChanged(sessionA);
        InvokeMaybeOpenUserDialogUiForEvent(host, sessionA);
        Assert.Null(host.PendingUserDialogUi);
        Assert.NotNull(sessionA.PendingUserDialog);

        var resumed = await host.ResumeSessionAsync(sessionA.PersistenceId);
        Assert.True(resumed.IsSuccess, resumed.IsError ? resumed.Error : null);
        Assert.NotNull(host.PendingUserDialogUi);
        Assert.Equal(sessionA.PersistenceId, host.PendingUserDialogUi.SessionPersistenceId);
        Assert.Equal(DysonUserDialogUiSource.RootPromptUserDialog, host.PendingUserDialogUi.Source);

        var formatted = DysonPromptUserDialog.FormatResult("Publish", skipped: false);
        var respond = sessionA.RespondToPromptUserDialog(formatted);
        Assert.True(respond.IsSuccess, respond.IsError ? respond.Error : null);
        var dialog = await dialogTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(dialog.IsSuccess, dialog.IsError ? dialog.Error : null);
    }

    private static void CancelBackgroundRunForTests(DysonAgentSession session)
    {
        var method = typeof(DysonAgentSession).GetMethod(
            "CancelBackgroundRun",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(session, null);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !predicate())
            await Task.Delay(10);
    }

    private static void RaiseParentEventsChanged(DysonAgentSession session)
    {
        var method = typeof(DysonAgentSession).GetMethod(
            "RaiseParentEventsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(session, null);
    }

    private static void InvokeMaybeOpenAskUiForEvent(DysonUiHost host, DysonAgentSession parent)
    {
        var method = typeof(DysonUiHost).GetMethod(
            "MaybeOpenAskUiForEvent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(
            host,
            [
                parent,
                new DysonAgentInterrupt
                {
                    Kind = DysonAgentInterruptKind.SubagentEvent,
                    SubagentId = 1,
                    EventId = Guid.NewGuid(),
                    EventKind = DysonAskQuestion.AskQuestionKind,
                    Payload = """{"questions":[{"prompt":"Steal?","options":["yes"]}]}""",
                },
            ]);
    }

    private static void InvokeMaybeOpenUserDialogUiForEvent(DysonUiHost host, DysonAgentSession parent)
    {
        var method = typeof(DysonUiHost).GetMethod(
            "MaybeOpenUserDialogUiForEvent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(
            host,
            [
                parent,
                new DysonAgentInterrupt
                {
                    Kind = DysonAgentInterruptKind.SubagentEvent,
                    SubagentId = 1,
                    EventId = Guid.NewGuid(),
                    EventKind = DysonPromptUserDialog.PromptUserDialogKind,
                    Payload =
                        """
                        {
                          "title": "Steal?",
                          "description": "Should not mount on B.",
                          "actions": [{ "label": "No" }]
                        }
                        """,
                },
            ]);
    }

    private static object[] EventTargets(object source, string eventName)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(
                eventName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is null)
                continue;

            if (field.GetValue(source) is not MulticastDelegate handlers)
                return [];

            return handlers.GetInvocationList()
                .Select(handler => handler.Target)
                .OfType<object>()
                .ToArray();
        }

        return [];
    }

    private sealed class HostHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _workRoot;

        private HostHarness(
            SqliteConnection connection,
            DysonDbAccessor accessor,
            IDysonSessionRepository sessions,
            IDysonModelRepository models,
            IDysonWorkDirectoryRepository workDirectories,
            IDysonWorkDirectoryConfigurationRepository workDirectoryConfigurations,
            IDysonSubjectSettingsRepository settings,
            IDysonConfiguredShellRepository shells,
            DysonPluginCatalogService catalog,
            DysonPluginLifecycleService lifecycle,
            DysonPluginContributionResolver contributions,
            DysonPluginMcpGrantService grantService,
            DysonPluginMcpResolver mcpResolver,
            DysonSessionRuntimeRegistry registry,
            DysonSessionRuntime runtime,
            CountingSessionFactory sessionFactory,
            WaitingSessionFactory? waitingFactory,
            Guid workDirectoryId,
            Guid slugId,
            string workRoot)
        {
            _connection = connection;
            Accessor = accessor;
            Sessions = sessions;
            Models = models;
            WorkDirectories = workDirectories;
            WorkDirectoryConfigurations = workDirectoryConfigurations;
            Settings = settings;
            Shells = shells;
            Catalog = catalog;
            Lifecycle = lifecycle;
            Contributions = contributions;
            GrantService = grantService;
            McpResolver = mcpResolver;
            Registry = registry;
            Runtime = runtime;
            SessionFactory = sessionFactory;
            WaitingFactory = waitingFactory;
            WorkDirectoryId = workDirectoryId;
            SlugId = slugId;
            _workRoot = workRoot;
        }

        public DysonDbAccessor Accessor { get; }
        public IDysonSessionRepository Sessions { get; }
        public IDysonModelRepository Models { get; }
        public IDysonWorkDirectoryRepository WorkDirectories { get; }
        public IDysonWorkDirectoryConfigurationRepository WorkDirectoryConfigurations { get; }
        public IDysonSubjectSettingsRepository Settings { get; }
        public IDysonConfiguredShellRepository Shells { get; }
        public DysonPluginCatalogService Catalog { get; }
        public DysonPluginLifecycleService Lifecycle { get; }
        public DysonPluginContributionResolver Contributions { get; }
        public DysonPluginMcpGrantService GrantService { get; }
        public DysonPluginMcpResolver McpResolver { get; }
        public DysonSessionRuntimeRegistry Registry { get; }
        public DysonSessionRuntime Runtime { get; }
        public CountingSessionFactory SessionFactory { get; }
        public WaitingSessionFactory? WaitingFactory { get; }
        public Guid WorkDirectoryId { get; }
        public Guid SlugId { get; }

        public static async Task<HostHarness> CreateAsync(bool waiting = false)
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subject = DysonFixedLocalSubjectContext.Instance;
            var sessions = DysonTempDb.Sessions(accessor, subject);
            var models = DysonTempDb.Models(accessor, subject);
            var workDirectories = DysonTempDb.WorkDirectories(accessor, subject);
            var workDirectoryConfigurations = DysonTempDb.WorkDirectoryConfigurations(accessor, subject);
            var settings = DysonTempDb.Settings(accessor, subject);
            var shells = DysonTempDb.Shells(accessor, subject);
            var plugins = DysonTempDb.Plugins(accessor, subject);
            var grants = new DysonPluginMcpGrantRepository(accessor, subject);
            var catalog = new DysonPluginCatalogService(plugins);
            var lifecycle = new DysonPluginLifecycleService(plugins);
            var contributions = new DysonPluginContributionResolver();
            var mcpResolver = new DysonPluginMcpResolver();
            var grantService = new DysonPluginMcpGrantService(plugins, grants, catalog, mcpResolver);

            var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-host-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workRoot);
            var workDirectory = await workDirectories.CreateAsync(workRoot, "HostRuntime");
            Assert.True(workDirectory.IsSuccess, workDirectory.IsError ? workDirectory.Error : null);

            var provider = await models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Local",
                ProviderKind = DysonProviderKinds.Demo,
            });
            Assert.True(provider.IsSuccess, provider.IsError ? provider.Error : null);
            var slug = await models.AddSlugAsync(provider.Value, "demo-host-runtime", "Demo Host Runtime");
            Assert.True(slug.IsSuccess, slug.IsError ? slug.Error : null);

            WaitingSessionFactory? waitingFactory = null;
            IDysonAgentSessionRuntimeFactory innerFactory;
            if (waiting)
            {
                waitingFactory = new WaitingSessionFactory(sessions);
                innerFactory = waitingFactory;
            }
            else
            {
                var configBuilder = new DysonUiAgentSessionRuntimeConfigBuilder(
                    workDirectories,
                    workDirectoryConfigurations,
                    settings,
                    shells,
                    models,
                    catalog,
                    contributions,
                    grantService,
                    mcpResolver);
                innerFactory = new DysonUiAgentSessionRuntimeFactory(
                    sessions,
                    models,
                    workDirectories,
                    new DysonWorkDirectoryService(workDirectories),
                    configBuilder);
            }

            var sessionFactory = new CountingSessionFactory(innerFactory);
            var scopeFactory = new TestScopeFactory(subject, sessions, sessionFactory);
            var registry = new DysonSessionRuntimeRegistry(scopeFactory);
            var created = await registry.GetOrCreateAsync(DysonSubjects.Local);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            return new HostHarness(
                connection,
                accessor,
                sessions,
                models,
                workDirectories,
                workDirectoryConfigurations,
                settings,
                shells,
                catalog,
                lifecycle,
                contributions,
                grantService,
                mcpResolver,
                registry,
                created.Value,
                sessionFactory,
                waitingFactory,
                workDirectory.Value,
                slug.Value,
                workRoot);
        }

        public DysonUiHost CreateHost(bool attachRuntime = true)
        {
            DysonUiRuntimeAttachment? attachment = attachRuntime
                ? new DysonUiRuntimeAttachment(Registry, DysonFixedLocalSubjectContext.Instance)
                : null;
            return new DysonUiHost(
                Sessions,
                Models,
                WorkDirectories,
                WorkDirectoryConfigurations,
                Settings,
                Shells,
                new HttpClient(),
                new DysonCliProxyHost(new HttpClient()),
                new DysonFilePreviewStore(),
                Catalog,
                Contributions,
                GrantService,
                McpResolver,
                Lifecycle,
                new ThemeService(new ThemeJsRuntime("light", "#ABC")),
                runtimeAttachment: attachment);
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
            try
            {
                Directory.Delete(_workRoot, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private sealed class TestScopeFactory(
        IDysonSubjectContext subject,
        IDysonSessionRepository sessions,
        IDysonAgentSessionRuntimeFactory sessionFactory) : IDysonSessionRuntimeScopeFactory
    {
        public Task<Result<RuntimeScopeLease, string>> CreateAsync(
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            var runtime = new DysonSessionRuntime(subject, sessions, sessionFactory);
            return Task.FromResult(
                Result<RuntimeScopeLease, string>.AsValue(new RuntimeScopeLease(subjectId, runtime)));
        }
    }

    internal sealed class CountingSessionFactory(IDysonAgentSessionRuntimeFactory inner)
        : IDysonAgentSessionRuntimeFactory
    {
        public int CreateCalls;
        public int LoadCalls;

        public Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
            DysonAgentSessionRuntimeCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CreateCalls);
            return inner.CreateRootAsync(request, cancellationToken);
        }

        public Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LoadCalls);
            return inner.LoadAsync(sessionId, cancellationToken);
        }
    }

    internal sealed class WaitingSessionFactory(IDysonSessionRepository sessions)
        : IDysonAgentSessionRuntimeFactory
    {
        public readonly TaskCompletionSource Entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ChildEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelObserved;
        public int ReportProcessingCalls;
        public bool EnqueueFollowUp;
        public IDysonSessionRepository Sessions => sessions;
        public Guid WorkDirectoryId { get; private set; }
        public Guid? ModelSlugId { get; private set; }

        public async Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
            DysonAgentSessionRuntimeCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            var created = await sessions.CreateSessionAsync(
                    new DysonSessionCreateRequest
                    {
                        RuntimeId = 0,
                        AgentMode = request.AgentMode,
                        ModelSlugId = request.ModelSlugId,
                        WorkDirectoryId = request.WorkDirectoryId,
                        Title = "waiting-root",
                        SystemPromptSnapshot = "waiting",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(created.Error);

            WorkDirectoryId = request.WorkDirectoryId;
            ModelSlugId = request.ModelSlugId;
            var session = new WaitingSession(this);
            session.SetPersistenceIdForTest(created.Value);
            return Result<DysonAgentSessionRuntimeLease, string>.AsValue(
                new DysonAgentSessionRuntimeLease(session));
        }

        public Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            _ = sessionId;
            _ = cancellationToken;
            return Task.FromResult(
                Result<DysonAgentSessionRuntimeLease, string>.AsError("Unexpected cold load."));
        }
    }

    private sealed class WaitingSession : DysonAgentSession
    {
        public const string FollowUpInstruction = "runtime-follow-up";

        private readonly WaitingSessionFactory _factory;

        public WaitingSession(WaitingSessionFactory factory, string agentMode = DysonAgentModes.Work)
            : base(agentMode, new DysonAgentSessionConfig(), new DemoDysonAgentProvider(slug: null))
        {
            _factory = factory;
        }

        public void SetPersistenceIdForTest(Guid persistenceId) => SetPersistenceId(persistenceId);

        public override async Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = initialTodos;
            _ = modelSlug;
            _ = reasoningEffort;
            _ = contextFiles;

            var child = new WaitingSession(_factory, agentMode);
            RegisterSubagent(child);
            var title = TitleFromTask(task);
            child.SetDisplayTitle(title);

            var created = await _factory.Sessions.CreateSessionAsync(
                    new DysonSessionCreateRequest
                    {
                        RuntimeId = child.Id,
                        ParentSessionId = PersistenceId,
                        AgentMode = agentMode,
                        ModelSlugId = _factory.ModelSlugId,
                        WorkDirectoryId = _factory.WorkDirectoryId,
                        Title = title,
                        SystemPromptSnapshot = "waiting-child",
                        Status = DysonSessionStatus.Active,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
                return Result<DysonStartSubagentResult, string>.AsError(created.Error);

            child.SetPersistenceIdForTest(created.Value);
            var runCts = new CancellationTokenSource();
            child.AttachBackgroundRun(runCts);
            KickOffChildPrompt(
                child,
                CreateNormalTurn("hold child"),
                runCts);

            return Result<DysonStartSubagentResult, string>.AsValue(new DysonStartSubagentResult
            {
                SubagentId = child.Id,
                PersistenceId = child.PersistenceId,
                AgentMode = agentMode,
                Title = title,
            });
        }

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => PromptAsync(prompt, [], cancellationToken);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => PromptHarnessTurnAsync(DysonAgentSession.CreateNormalTurn(prompt), cancellationToken);

        public override async Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
        {
            if (Parent is not null)
            {
                _factory.ChildEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return VoidResult<string>.Success;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _factory.CancelObserved);
                    return VoidResult<string>.AsError("Prompt was cancelled.");
                }
            }

            _factory.Entered.TrySetResult();
            try
            {
                await _factory.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _factory.CancelObserved);
                return VoidResult<string>.AsError("Prompt was cancelled.");
            }

            turn.AssistantText = "done";
            AddTurn(turn);
            if (_factory.EnqueueFollowUp
                && !string.Equals(turn.Instruction, FollowUpInstruction, StringComparison.Ordinal))
            {
                EnqueuePendingTurn(CreateNormalTurn(FollowUpInstruction));
            }

            return VoidResult<string>.Success;
        }

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
        {
            _ = interrupt;
            _ = title;
            _ = cancellationToken;
            Interlocked.Increment(ref _factory.ReportProcessingCalls);
            return Task.FromResult(VoidResult<string>.Success);
        }

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
        {
            _ = instruction;
            _ = cancellationToken;
            Interlocked.Increment(ref _factory.ReportProcessingCalls);
            return Task.FromResult(VoidResult<string>.Success);
        }

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThemeJsRuntime(string theme, string accent) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? value = identifier switch
            {
                "dysonTheme.get" => null,
                "dysonTheme.getResolved" => new { theme, accentHex = accent },
                "dysonTheme.apply" => null,
                _ => throw new InvalidOperationException($"Unexpected JS call: {identifier}"),
            };

            if (value is null)
                return ValueTask.FromResult(default(TValue)!);

            var json = JsonSerializer.Serialize(value);
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
    }
}

using DysonHarness;

namespace Harness.Tests;

public class DysonSessionEventPublisherTests
{
    [Fact]
    public void RegisterForTest_publishes_spawn_on_parent_and_child_keys()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var parent = CreateSession(parentId, "parent");
        var child = CreateSession(childId, "child-title");
        using var attach = Attach(publisher, parent);

        var parentSpawns = new List<DysonSubagentSpawnedEvent>();
        var childSpawns = new List<DysonSubagentSpawnedEvent>();
        Assert.True(bus.Subscribe<DysonSubagentSpawnedEvent>(
            DysonBusScopes.Session(parentId), parentSpawns.Add).IsSuccess);
        Assert.True(bus.Subscribe<DysonSubagentSpawnedEvent>(
            DysonBusScopes.Session(childId), childSpawns.Add).IsSuccess);

        parent.RegisterForTest(child);

        Assert.Single(parentSpawns);
        Assert.Single(childSpawns);
        Assert.Equal(parentSpawns[0], childSpawns[0]);
        Assert.Equal(parentId, parentSpawns[0].ParentPersistenceId);
        Assert.Equal(childId, parentSpawns[0].ChildPersistenceId);
        Assert.Equal(child.Id, parentSpawns[0].RuntimeId);
        Assert.Equal("child-title", parentSpawns[0].Title);
        Assert.Equal(DysonAgentModes.Work, parentSpawns[0].AgentMode);
    }

    [Fact]
    public void TryAcceptSubagentReport_publishes_completed_status_on_child_and_parent()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (parent, child) = AttachParentChild(publisher, bus, out var parentStatus, out var childStatus);

        Assert.True(child.TryAcceptSubagentReport(DysonSessionStatus.Completed, "done"));

        Assert.Single(parentStatus);
        Assert.Single(childStatus);
        Assert.Equal(parentStatus[0], childStatus[0]);
        AssertStatus(childStatus[0], child, DysonSessionStatus.Completed, isRunning: false, "done");
    }

    [Fact]
    public void TryMarkTerminal_publishes_stopped_status()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (parent, child) = AttachParentChild(publisher, bus, out var parentStatus, out var childStatus);

        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Stopped, "halt"));

        Assert.Single(parentStatus);
        Assert.Single(childStatus);
        AssertStatus(childStatus[0], child, DysonSessionStatus.Stopped, isRunning: false, "halt");
    }

    [Fact]
    public void TryReopenForNewParentTask_after_completed_publishes_active()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (_, child) = AttachParentChild(publisher, bus, out _, out var childStatus);

        Assert.True(child.TryAcceptSubagentReport(DysonSessionStatus.Completed, "done"));
        childStatus.Clear();
        Assert.True(child.TryReopenForNewParentTask());

        Assert.Single(childStatus);
        AssertStatus(childStatus[0], child, DysonSessionStatus.Active, isRunning: true, "done");
    }

    [Fact]
    public void Failed_to_Completed_publishes_status_change()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (_, child) = AttachParentChild(publisher, bus, out _, out var childStatus);

        Assert.True(child.TryAcceptSubagentReport(DysonSessionStatus.Failed, "boom"));
        childStatus.Clear();
        Assert.True(child.TryAcceptSubagentReport(DysonSessionStatus.Completed, "recovered"));

        Assert.Single(childStatus);
        AssertStatus(childStatus[0], child, DysonSessionStatus.Completed, isRunning: false, "recovered");
    }

    [Fact]
    public void TryMarkTerminal_twice_does_not_publish_second_status()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (_, child) = AttachParentChild(publisher, bus, out _, out var childStatus);

        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Stopped, "halt"));
        Assert.False(child.TryMarkTerminal(DysonSessionStatus.Failed, "again"));

        Assert.Single(childStatus);
        AssertStatus(childStatus[0], child, DysonSessionStatus.Stopped, isRunning: false, "halt");
    }

    [Fact]
    public async Task Grandchild_spawned_after_attach_is_hooked()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandId = Guid.NewGuid();
        var parent = CreateSession(parentId, "parent");
        using var attach = Attach(publisher, parent);

        var child = CreateSession(childId, "child");
        parent.RegisterForTest(child);
        var grand = CreateSession(grandId, "grand");
        child.RegisterForTest(grand);

        var status = new List<DysonSubagentStatusChangedEvent>();
        var activity = new List<DysonSubagentActivityChangedEvent>();
        Assert.True(bus.Subscribe<DysonSubagentStatusChangedEvent>(
            DysonBusScopes.Session(grandId), status.Add).IsSuccess);
        Assert.True(bus.Subscribe<DysonSubagentActivityChangedEvent>(
            DysonBusScopes.Session(grandId), activity.Add).IsSuccess);

        grand.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            AgentTitle = "step",
            StartedUtc = DateTime.UtcNow,
        });
        await WaitUntilAsync(() => activity.Count > 0, TimeSpan.FromMilliseconds(200));
        Assert.NotEmpty(activity);

        Assert.True(grand.TryMarkTerminal(DysonSessionStatus.Completed, "g-done"));
        Assert.Single(status);
        AssertStatus(status[0], grand, DysonSessionStatus.Completed, isRunning: false, "g-done");
    }

    [Fact]
    public void Existing_children_at_attach_are_hooked_without_spawn_event()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var parent = CreateSession(parentId, "parent");
        var child = CreateSession(childId, "child");
        parent.RegisterForTest(child);

        var spawns = new List<DysonSubagentSpawnedEvent>();
        Assert.True(bus.Subscribe<DysonSubagentSpawnedEvent>(
            DysonBusScopes.Session(parentId), spawns.Add).IsSuccess);
        Assert.True(bus.Subscribe<DysonSubagentSpawnedEvent>(
            DysonBusScopes.Session(childId), spawns.Add).IsSuccess);

        using var attach = Attach(publisher, parent);
        Assert.Empty(spawns);

        var childStatus = new List<DysonSubagentStatusChangedEvent>();
        Assert.True(bus.Subscribe<DysonSubagentStatusChangedEvent>(
            DysonBusScopes.Session(childId), childStatus.Add).IsSuccess);
        Assert.True(child.TryAcceptSubagentReport(DysonSessionStatus.Completed, "restored"));
        Assert.Single(childStatus);
        AssertStatus(childStatus[0], child, DysonSessionStatus.Completed, isRunning: false, "restored");
    }

    [Fact]
    public void AddTurn_publishes_turn_added_on_session_key()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var sessionId = Guid.NewGuid();
        var session = CreateSession(sessionId, "root");
        using var attach = Attach(publisher, session);

        var turns = new List<DysonSessionTurnAddedEvent>();
        Assert.True(bus.Subscribe<DysonSessionTurnAddedEvent>(
            DysonBusScopes.Session(sessionId), turns.Add).IsSuccess);

        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "go",
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(turn);

        Assert.Single(turns);
        Assert.Equal(sessionId, turns[0].PersistenceId);
        Assert.Equal(turn.Id, turns[0].TurnId);
        Assert.Equal(DysonAgentTurnKind.Normal, turns[0].Kind);
    }

    [Fact]
    public async Task Identical_activity_tuples_are_deduped_until_step_title_changes()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var sessionId = Guid.NewGuid();
        var session = CreateSession(sessionId, "root");
        using var attach = Attach(publisher, session);

        var activity = new List<DysonSubagentActivityChangedEvent>();
        Assert.True(bus.Subscribe<DysonSubagentActivityChangedEvent>(
            DysonBusScopes.Session(sessionId), activity.Add).IsSuccess);

        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            AgentTitle = "A",
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(turn);
        turn.AppendStreamingDelta("hello");

        await Task.Delay(200);
        Assert.True(activity.Count <= 1);
        Assert.Single(activity);
        Assert.Equal("root", activity[0].Title);
        Assert.Equal("A", activity[0].LatestTurnStepTitle);
        Assert.True(activity[0].IsRunning);

        turn.AgentTitle = "B";
        turn.AppendStreamingDelta(" world");
        await WaitUntilAsync(() => activity.Count >= 2, TimeSpan.FromMilliseconds(200));

        Assert.Equal(2, activity.Count);
        Assert.Equal("B", activity[1].LatestTurnStepTitle);
    }

    [Fact]
    public void Attach_token_dispose_stops_publishing_and_double_dispose_is_noop()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (parent, child) = AttachParentChild(publisher, bus, out _, out var childStatus, out var token);

        token.Dispose();
        token.Dispose();

        Assert.True(child.TryAcceptSubagentReport(DysonSessionStatus.Completed, "done"));
        Assert.Empty(childStatus);

        parent.RegisterForTest(CreateSession(Guid.NewGuid(), "late"));
        var spawns = new List<DysonSubagentSpawnedEvent>();
        Assert.True(bus.Subscribe<DysonSubagentSpawnedEvent>(
            DysonBusScopes.Session(parent.PersistenceId), spawns.Add).IsSuccess);
        parent.RegisterForTest(CreateSession(Guid.NewGuid(), "later"));
        Assert.Empty(spawns);
    }

    [Fact]
    public void Attach_after_publisher_dispose_is_error()
    {
        var bus = new DysonMessageBus();
        var publisher = new DysonSessionEventPublisher(bus);
        publisher.Dispose();

        var result = publisher.Attach(CreateSession(Guid.NewGuid(), "root"));
        Assert.True(result.IsError);
    }

    private static (StubSession Parent, StubSession Child) AttachParentChild(
        DysonSessionEventPublisher publisher,
        DysonMessageBus bus,
        out List<DysonSubagentStatusChangedEvent> parentStatus,
        out List<DysonSubagentStatusChangedEvent> childStatus)
        => AttachParentChild(publisher, bus, out parentStatus, out childStatus, out _);

    private static (StubSession Parent, StubSession Child) AttachParentChild(
        DysonSessionEventPublisher publisher,
        DysonMessageBus bus,
        out List<DysonSubagentStatusChangedEvent> parentStatus,
        out List<DysonSubagentStatusChangedEvent> childStatus,
        out IDisposable token)
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var parent = CreateSession(parentId, "parent");
        var child = CreateSession(childId, "child");
        parent.RegisterForTest(child);
        token = Attach(publisher, parent);

        parentStatus = [];
        childStatus = [];
        Assert.True(bus.Subscribe<DysonSubagentStatusChangedEvent>(
            DysonBusScopes.Session(parentId), parentStatus.Add).IsSuccess);
        Assert.True(bus.Subscribe<DysonSubagentStatusChangedEvent>(
            DysonBusScopes.Session(childId), childStatus.Add).IsSuccess);
        return (parent, child);
    }

    private static IDisposable Attach(DysonSessionEventPublisher publisher, DysonAgentSession root)
    {
        var attached = publisher.Attach(root);
        Assert.True(attached.IsSuccess, attached.IsError ? attached.Error : "");
        return attached.Value;
    }

    private static StubSession CreateSession(Guid persistenceId, string title)
    {
        var session = new StubSession();
        session.SetPersistenceIdForTest(persistenceId);
        session.SetDisplayTitleForTest(title);
        return session;
    }

    private static void AssertStatus(
        DysonSubagentStatusChangedEvent evt,
        DysonAgentSession session,
        DysonSessionStatus status,
        bool isRunning,
        string? summary)
    {
        Assert.Equal(session.PersistenceId, evt.PersistenceId);
        Assert.Equal(session.Parent?.PersistenceId, evt.ParentPersistenceId);
        Assert.Equal(session.Id, evt.RuntimeId);
        Assert.Equal(status, evt.Status);
        Assert.Equal(isRunning, evt.IsRunning);
        Assert.Equal(summary, evt.Summary);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !predicate())
            await Task.Delay(10);

        Assert.True(predicate());
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        public void SetPersistenceIdForTest(Guid persistenceId) => SetPersistenceId(persistenceId);

        public void SetDisplayTitleForTest(string? title) => SetDisplayTitle(title);

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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
            => Task.FromResult(VoidResult<string>.Success);

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

using DysonHarness;

namespace Harness.Tests;

/// <summary>Plan 370: SubagentTasks pill (counts, card text, running state, live watch, validation).</summary>
public class DysonA2uiSubagentTasksTests
{
    private const string P0 = "/messages/0/createSurface/components/0";

    private static DysonSessionTodo Todo(int sequence, DysonSessionTodoStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TaskCode = "t" + sequence,
        DisplayName = "t" + sequence,
        Status = status,
        Sequence = sequence,
    };

    private static (DysonPlanToolStubSession Parent, DysonPlanToolStubSession Child) ParentWithChild()
    {
        var parent = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        parent.SetPersistenceIdForTest(Guid.NewGuid());
        var child = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        child.SetPersistenceIdForTest(Guid.NewGuid());
        parent.RegisterForTest(child);
        return (parent, child);
    }

    private static Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> Parse(string props, string catalogId = DysonA2uiProtocol.DysonCatalogId) =>
        DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create(
            "{\"id\":\"root\",\"component\":\"SubagentTasks\"" + props + "}",
            ",\"catalogId\":\"" + catalogId + "\"",
            "s1"));

    private static void AssertFails(Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> result, string path)
    {
        Assert.True(result.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.Equal(path, result.Error.Path);
    }

    [Fact]
    public void Count_is_complete_over_total_and_zero_todos_is_0_of_0()
    {
        Assert.Equal((0, 0), DysonA2uiSubagentTasks.Count([]));
        Assert.Equal((2, 4), DysonA2uiSubagentTasks.Count([
            Todo(1, DysonSessionTodoStatus.Pending),
            Todo(2, DysonSessionTodoStatus.Ongoing),
            Todo(3, DysonSessionTodoStatus.Complete),
            Todo(4, DysonSessionTodoStatus.Complete),
        ]));
    }

    [Fact]
    public void Build_gives_card_text_for_missing_owner_unknown_id_and_stopped_agent()
    {
        Assert.Equal("No agent session.", DysonA2uiSubagentTasks.Build(null, 1).Message);

        var (parent, child) = ParentWithChild();
        Assert.Equal($"Agent #{child.Id + 1} not found.", DysonA2uiSubagentTasks.Build(parent, child.Id + 1).Message);

        var live = DysonA2uiSubagentTasks.Build(parent, child.Id);
        Assert.Null(live.Message);
        Assert.Equal("0/0", live.Pill);

        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Stopped, "halt"));
        Assert.Equal($"Agent #{child.Id} was stopped.", DysonA2uiSubagentTasks.Build(parent, child.Id).Message);
    }

    [Theory]
    [InlineData(DysonSessionStatus.Completed)]
    [InlineData(DysonSessionStatus.Failed)]
    [InlineData(DysonSessionStatus.Blocked)]
    [InlineData(DysonSessionStatus.Interrupted)]
    public void Build_sets_running_only_when_the_child_is_active(DysonSessionStatus terminal)
    {
        var (parent, child) = ParentWithChild();
        var active = DysonA2uiSubagentTasks.Build(parent, child.Id);
        Assert.True(active.IsRunning);
        Assert.Equal(DysonSessionStatus.Active, active.Status);

        child.RestoreTodos([Todo(1, DysonSessionTodoStatus.Complete)]);
        Assert.True(DysonA2uiSubagentTasks.Build(parent, child.Id).IsRunning);

        if (terminal == DysonSessionStatus.Interrupted)
            Assert.True(child.TryMarkTerminal(terminal, "x"));
        else
            Assert.True(child.TryAcceptSubagentReport(terminal, "x"));

        var done = DysonA2uiSubagentTasks.Build(parent, child.Id);
        Assert.False(done.IsRunning);
        Assert.Equal(terminal, done.Status);
        Assert.Equal("1/1", done.Pill);
    }

    [Fact]
    public void Watch_rebuilds_the_card_when_the_child_todos_change()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (parent, child) = ParentWithChild();
        var attached = publisher.Attach(parent);
        Assert.True(attached.IsSuccess);
        using var token = attached.Value;

        var cards = new List<DysonA2uiSubagentTasksCard>();
        var watch = DysonA2uiSubagentTasks.Watch(bus, parent, child.Id, cards.Add);
        Assert.True(watch.IsSuccess);

        child.RestoreTodos([Todo(1, DysonSessionTodoStatus.Pending), Todo(2, DysonSessionTodoStatus.Complete)]);
        var card = Assert.Single(cards);
        Assert.Equal("1/2", card.Pill);
        Assert.True(card.IsRunning);

        Assert.True(child.TryAcceptSubagentReport(DysonSessionStatus.Completed, "done"));
        Assert.Equal(2, cards.Count);
        Assert.False(cards[1].IsRunning);
        Assert.Equal("1/2", cards[1].Pill);

        watch.Value.Dispose();
        child.RestoreTodos([]);
        Assert.Equal(2, cards.Count);

        var unknown = DysonA2uiSubagentTasks.Watch(bus, parent, child.Id + 5, cards.Add);
        Assert.True(unknown.IsSuccess);
        unknown.Value.Dispose();
    }

    [Fact]
    public void Build_projects_the_open_target_and_the_agents_own_children_in_spawn_order()
    {
        var (parent, child) = ParentWithChild();
        var card = DysonA2uiSubagentTasks.Build(parent, child.Id);
        Assert.Equal(child.PersistenceId, card.PersistenceId);
        Assert.Empty(card.ChildIds);

        var first = new DysonPlanToolStubSession(DysonAgentModes.Explore);
        first.SetPersistenceIdForTest(Guid.NewGuid());
        var second = new DysonPlanToolStubSession(DysonAgentModes.Drone);
        second.SetPersistenceIdForTest(Guid.NewGuid());
        var unpersisted = new DysonPlanToolStubSession(DysonAgentModes.Drone); // hidden, as in the Meta roster
        child.RegisterForTest(first);
        child.RegisterForTest(second);
        child.RegisterForTest(unpersisted);

        Assert.Equal([first.Id, second.Id], DysonA2uiSubagentTasks.Build(parent, child.Id).ChildIds);
        // The parent-level card lists only direct children, not grandchildren.
        Assert.Empty(DysonA2uiSubagentTasks.ChildIdsOf(parent).Except([child.Id]));
        // A child row resolves against its own parent, the same Build.
        Assert.Equal(first.PersistenceId, DysonA2uiSubagentTasks.Build(child, first.Id).PersistenceId);

        // No session to open for unknown / stopped agents.
        Assert.Equal(Guid.Empty, DysonA2uiSubagentTasks.Build(parent, child.Id + 99).PersistenceId);
        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Stopped, "halt"));
        Assert.Equal(Guid.Empty, DysonA2uiSubagentTasks.Build(parent, child.Id).PersistenceId);
    }

    [Fact]
    public void ToggleLabel_names_the_count_and_the_hide_state()
    {
        Assert.Equal("Show 1 agent", DysonA2uiSubagentTasks.ToggleLabel(1, false));
        Assert.Equal("Show 3 agents", DysonA2uiSubagentTasks.ToggleLabel(3, false));
        Assert.Equal("Hide agents", DysonA2uiSubagentTasks.ToggleLabel(3, true));
    }

    [Fact]
    public void Watch_rebuilds_the_card_when_the_agent_spawns_a_child()
    {
        using var bus = new DysonMessageBus();
        using var publisher = new DysonSessionEventPublisher(bus);
        var (parent, child) = ParentWithChild();
        var attached = publisher.Attach(parent);
        Assert.True(attached.IsSuccess);
        using var token = attached.Value;

        var cards = new List<DysonA2uiSubagentTasksCard>();
        var watch = DysonA2uiSubagentTasks.Watch(bus, parent, child.Id, cards.Add);
        Assert.True(watch.IsSuccess);
        using var _ = watch.Value;

        var grandchild = new DysonPlanToolStubSession(DysonAgentModes.Explore);
        grandchild.SetPersistenceIdForTest(Guid.NewGuid());
        child.RegisterForTest(grandchild);

        Assert.NotEmpty(cards);
        Assert.Equal([grandchild.Id], cards[^1].ChildIds);
    }

    [Fact]
    public void Validator_accepts_SubagentTasks_only_in_the_Dyson_catalog()
    {
        Assert.True(Parse(",\"agentId\":3").IsSuccess);
        Assert.True(Parse(",\"agentId\":3", DysonA2uiProtocol.PlaygroundCatalogId).IsSuccess);
        AssertFails(Parse(",\"agentId\":3", DysonA2uiProtocol.BasicCatalogId), P0 + "/component");
        AssertFails(Parse(""), P0 + "/agentId");
        AssertFails(Parse(",\"agentId\":0"), P0 + "/agentId");
        AssertFails(Parse(",\"agentId\":\"3\""), P0 + "/agentId");
        AssertFails(Parse(",\"agentId\":{\"@path\":\"/a\"}"), P0 + "/agentId");
        AssertFails(Parse(",\"agentId\":3,\"title\":\"x\""), P0 + "/title");
    }
}

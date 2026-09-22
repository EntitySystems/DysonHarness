using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Plan-tool mutations publish <see cref="DysonPlansChangedEvent"/> once on the work-directory
/// bus scope; a null bus is a silent no-op.
/// </summary>
public class DysonPlanBusPublishTests
{
    [Fact]
    public async Task Each_plan_mutation_publishes_once_on_the_workdir_scope()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var bus = new DysonMessageBus();
        var hits = new List<DysonPlansChangedEvent>();
        var subscribed = bus.Subscribe<DysonPlansChangedEvent>(
            DysonBusScopes.WorkDirectory(fixture.WorkDirectoryId),
            hits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var _ = subscribed.Value;

        using var http = new HttpClient();
        var drone = BindBus(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), bus);
        var meta = BindBus(new DysonPlanToolStubSession(DysonAgentModes.MetaAgent), bus);
        var droneExecutor = await fixture.ExecutorAsync(drone, http);
        var metaExecutor = await fixture.ExecutorAsync(meta, http);

        var created = await droneExecutor.ExecuteAsync(
            Call("SubmitMetaPlan", """{"title":"First","markdown":"# v1"}"""));
        Assert.False(created.IsError, created.Content);
        var planId = JsonDocument.Parse(created.Content).RootElement.GetProperty("planId").GetInt64();
        AssertPublishedOnce(hits, fixture.WorkDirectoryId, planId);

        var revised = await droneExecutor.ExecuteAsync(
            Call("SubmitMetaPlan", $$"""{"title":"Second","markdown":"# v2","planId":{{planId}}}"""));
        Assert.False(revised.IsError, revised.Content);
        AssertPublishedOnce(hits, fixture.WorkDirectoryId, planId);

        var set = await metaExecutor.ExecuteAsync(
            Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"stale"}"""));
        Assert.False(set.IsError, set.Content);
        AssertPublishedOnce(hits, fixture.WorkDirectoryId, planId);

        var deleted = await metaExecutor.ExecuteAsync(
            Call("DeletePlan", $$"""{"planId":{{planId}},"reason":"superseded"}"""));
        Assert.False(deleted.IsError, deleted.Content);
        AssertPublishedOnce(hits, fixture.WorkDirectoryId, planId);

        var buildId = await fixture.CreatePlanAsync("Build me");
        var built = await metaExecutor.ExecuteAsync(
            Call("BeginBuildPlan", $$"""{"planId":{{buildId}}}"""));
        Assert.False(built.IsError, built.Content);
        AssertPublishedOnce(hits, fixture.WorkDirectoryId, buildId);

        var second = await metaExecutor.ExecuteAsync(
            Call("BeginBuildPlan", $$"""{"planId":{{buildId}}}"""));
        Assert.True(second.IsError);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Failed_mutations_and_reads_do_not_publish()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var bus = new DysonMessageBus();
        var hits = new List<DysonPlansChangedEvent>();
        var subscribed = bus.Subscribe<DysonPlansChangedEvent>(
            DysonBusScopes.WorkDirectory(fixture.WorkDirectoryId),
            hits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var _ = subscribed.Value;

        using var http = new HttpClient();
        var drone = BindBus(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), bus);
        var meta = BindBus(new DysonPlanToolStubSession(DysonAgentModes.MetaAgent), bus);
        var droneExecutor = await fixture.ExecutorAsync(drone, http);
        var metaExecutor = await fixture.ExecutorAsync(meta, http);
        var otherExecutor = await fixture.ExecutorAsync(meta, http, fixture.OtherWorkDirectoryId);

        var planId = await fixture.CreatePlanAsync();
        var buildingId = await fixture.CreatePlanAsync(status: DysonPlanStatus.Building);

        var listed = await metaExecutor.ExecuteAsync(Call("ListPlans", "{}"));
        Assert.False(listed.IsError, listed.Content);

        var read = await droneExecutor.ExecuteAsync(
            Call("ReadMetaPlan", $$"""{"planId":{{planId}}}"""));
        Assert.False(read.IsError, read.Content);

        var draft = await metaExecutor.ExecuteAsync(
            Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"draft"}"""));
        Assert.True(draft.IsError);

        var cross = await otherExecutor.ExecuteAsync(
            Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"stale"}"""));
        Assert.True(cross.IsError);

        var refused = await metaExecutor.ExecuteAsync(
            Call("DeletePlan", $$"""{"planId":{{buildingId}},"reason":"abandoned"}"""));
        Assert.True(refused.IsError);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Other_workdir_scope_does_not_receive_the_event()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var bus = new DysonMessageBus();
        var otherHits = new List<DysonPlansChangedEvent>();
        var subscribed = bus.Subscribe<DysonPlansChangedEvent>(
            DysonBusScopes.WorkDirectory(fixture.OtherWorkDirectoryId),
            otherHits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var _ = subscribed.Value;

        using var http = new HttpClient();
        var drone = BindBus(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), bus);
        var executor = await fixture.ExecutorAsync(drone, http);
        var created = await executor.ExecuteAsync(
            Call("SubmitMetaPlan", """{"title":"Scoped","markdown":"# body"}"""));
        Assert.False(created.IsError, created.Content);
        Assert.Empty(otherHits);
    }

    [Fact]
    public async Task Null_bus_does_not_throw()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        Assert.Null(drone.Config.Bus);
        var executor = await fixture.ExecutorAsync(drone, http);
        var created = await executor.ExecuteAsync(
            Call("SubmitMetaPlan", """{"title":"Quiet","markdown":"# body"}"""));
        Assert.False(created.IsError, created.Content);

        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        Assert.Null(meta.Config.Bus);
        var metaExecutor = await fixture.ExecutorAsync(meta, http);
        var planId = JsonDocument.Parse(created.Content).RootElement.GetProperty("planId").GetInt64();
        var set = await metaExecutor.ExecuteAsync(
            Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"completed"}"""));
        Assert.False(set.IsError, set.Content);
        var deleted = await metaExecutor.ExecuteAsync(
            Call("DeletePlan", $$"""{"planId":{{planId}},"reason":"done"}"""));
        Assert.False(deleted.IsError, deleted.Content);
    }

    private static DysonPlanToolStubSession BindBus(DysonPlanToolStubSession session, DysonMessageBus bus)
    {
        session.Config.Bus = bus;
        return session;
    }

    private static void AssertPublishedOnce(
        List<DysonPlansChangedEvent> hits,
        Guid workDirectoryId,
        long planId)
    {
        var evt = Assert.Single(hits);
        Assert.Equal(workDirectoryId, evt.WorkDirectoryId);
        Assert.Equal(planId, evt.PlanId);
        hits.Clear();
    }

    private static DysonToolCall Call(string name, string args) => new()
    {
        CallId = "1",
        ToolName = name,
        Stage = 0,
        ArgumentsJson = args,
    };
}

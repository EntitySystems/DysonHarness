using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Plan 68 guard: at most <see cref="DysonMetaAgentTools.MaxRunningWorktreeDrones"/> live
/// <c>useWorktree: true</c> Meta Agent Drones per root session; spawns report running siblings.
/// </summary>
public class DysonWorktreeDroneCapTests
{
    [Fact]
    public async Task Fourth_worktree_drone_is_refused_and_false_drones_are_not_capped()
    {
        using var http = new HttpClient();
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var ids = Enumerable.Range(0, DysonMetaAgentTools.MaxRunningWorktreeDrones)
            .Select(_ => RegisterDrone(meta, worktree: true).Id)
            .ToList();
        var executor = await fixture.ExecutorAsync(meta, http);

        var refused = await executor.ExecuteAsync(DroneCall("1", """{"task":"edit","useWorktree":true}"""));
        Assert.True(refused.IsError);
        Assert.Contains($"the limit is {DysonMetaAgentTools.MaxRunningWorktreeDrones}", refused.Content, StringComparison.Ordinal);
        foreach (var id in ids)
            Assert.Contains($"#{id}", refused.Content, StringComparison.Ordinal);
        Assert.Null(meta.LastSpawned);

        var shared = await executor.ExecuteAsync(DroneCall("2", """{"task":"run tests","useWorktree":false}"""));
        Assert.False(shared.IsError, shared.Content);
        Assert.NotNull(meta.LastSpawned);
    }

    [Fact]
    public async Task Terminal_and_branchless_drones_are_not_counted()
    {
        using var http = new HttpClient();
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        RegisterDrone(meta, worktree: true);
        RegisterDrone(meta, worktree: true);
        RegisterDrone(meta, worktree: true).TryMarkTerminal(DysonSessionStatus.Failed, "Merge conflict.");
        RegisterDrone(meta, worktree: false); // useWorktree false / existingWorktreePath resolver
        var executor = await fixture.ExecutorAsync(meta, http);

        var ok = await executor.ExecuteAsync(DroneCall("1", """{"task":"edit","useWorktree":true}"""));
        Assert.False(ok.IsError, ok.Content);
    }

    [Fact]
    public async Task BeginBuildPlan_new_spawn_is_capped_but_agentId_reuse_is_not()
    {
        using var http = new HttpClient();
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var planId = await fixture.CreatePlanAsync("Capped");
        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var drones = Enumerable.Range(0, DysonMetaAgentTools.MaxRunningWorktreeDrones)
            .Select(_ => RegisterDrone(meta, worktree: true))
            .ToList();
        var executor = await fixture.ExecutorAsync(meta, http);

        var refused = await executor.ExecuteAsync(BuildCall("1", $$"""{"planId":{{planId}}}"""));
        Assert.True(refused.IsError);
        Assert.Contains("the limit is", refused.Content, StringComparison.Ordinal);
        Assert.Null(meta.LastSpawned);
        var stored = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
        Assert.False(stored.IsError);
        Assert.NotEqual(DysonPlanStatus.Building, stored.Value.Status);

        var reused = await executor.ExecuteAsync(BuildCall("2", $$"""{"planId":{{planId}},"agentId":{{drones[0].Id}}}"""));
        Assert.False(reused.IsError, reused.Content);
    }

    [Fact]
    public async Task Result_lists_running_siblings_only_when_there_are_some()
    {
        using var http = new HttpClient();
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var executor = await fixture.ExecutorAsync(meta, http);

        var first = await executor.ExecuteAsync(DroneCall("1", """{"task":"edit a","useWorktree":true}"""));
        Assert.False(first.IsError, first.Content);
        int firstId;
        using (var doc = JsonDocument.Parse(first.Content))
        {
            firstId = doc.RootElement.GetProperty("droneId").GetInt32();
            Assert.False(doc.RootElement.TryGetProperty("runningWorktreeDrones", out _));
            Assert.False(doc.RootElement.TryGetProperty("notice", out _));
        }

        var second = await executor.ExecuteAsync(DroneCall("2", """{"task":"edit b","useWorktree":true}"""));
        Assert.False(second.IsError, second.Content);
        using (var doc = JsonDocument.Parse(second.Content))
        {
            var running = doc.RootElement.GetProperty("runningWorktreeDrones").EnumerateArray().Select(e => e.GetInt32()).ToList();
            Assert.Equal([firstId], running);
            Assert.Contains($"#{firstId}", doc.RootElement.GetProperty("notice").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Concurrent_spawns_cannot_pass_the_cap_together()
    {
        using var http = new HttpClient();
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        for (var i = 0; i < DysonMetaAgentTools.MaxRunningWorktreeDrones - 1; i++)
            RegisterDrone(meta, worktree: true);
        var executor = await fixture.ExecutorAsync(meta, http);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            executor.ExecuteAsync(DroneCall($"c{i}", """{"task":"edit","useWorktree":true}"""))));

        Assert.Equal(1, results.Count(r => !r.IsError));
        Assert.Equal(3, results.Count(r => r.IsError));
    }

    private static DysonPlanToolStubSession RegisterDrone(DysonPlanToolStubSession meta, bool worktree)
    {
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        drone.SetPersistenceIdForTest(Guid.NewGuid());
        if (worktree)
            drone.WorktreeBranch = "dyson/" + Guid.NewGuid().ToString("N")[..8];
        meta.RegisterForTest(drone);
        return drone;
    }

    private static DysonToolCall DroneCall(string callId, string argumentsJson) => new()
    {
        CallId = callId,
        ToolName = "StartAsyncMetaAgentDrone",
        Stage = 0,
        ArgumentsJson = argumentsJson,
    };

    private static DysonToolCall BuildCall(string callId, string argumentsJson) => new()
    {
        CallId = callId,
        ToolName = "BeginBuildPlan",
        Stage = 0,
        ArgumentsJson = argumentsJson,
    };
}

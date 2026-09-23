using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// BeginBuildPlan dispatches via CreateMetaAgentDroneChildAsync, sets Building + BuildAgentId,
/// refuses a second concurrent builder, and does not use DysonBeginBuildPlanFlow.
/// </summary>
public class DysonBeginBuildPlanToolTests
{
    [Fact]
    public async Task Spawns_drone_sets_building_and_refuses_second_concurrent_build()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        const string secret = "SECRET_PLAN_BODY_MUST_NOT_LEAK";
        var planId = await fixture.CreatePlanAsync("Ship it", secret);

        using var http = new HttpClient();
        var session = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var executor = await fixture.ExecutorAsync(session, http);

        var started = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "BeginBuildPlan",
            Stage = 0,
            ArgumentsJson = $$"""{"planId":{{planId}},"extraInstructions":"prefer existing helpers"}""",
        });
        Assert.False(started.IsError, started.Content);

        using (var doc = JsonDocument.Parse(started.Content))
        {
            Assert.Equal(planId, doc.RootElement.GetProperty("planId").GetInt64());
            Assert.Equal("building", doc.RootElement.GetProperty("status").GetString());
            Assert.True(doc.RootElement.GetProperty("agentId").GetInt32() >= 1);
            Assert.NotEqual(Guid.Empty, doc.RootElement.GetProperty("persistenceId").GetGuid());
        }

        var spawned = session.LastSpawned;
        Assert.NotNull(spawned);
        Assert.Equal(DysonAgentModes.MetaAgentDrone, spawned.Mode);
        var brief = session.LastSpawnedTask;
        Assert.False(string.IsNullOrWhiteSpace(brief));
        Assert.Contains(planId.ToString(), brief, StringComparison.Ordinal);
        Assert.Contains("ReadMetaPlan", brief, StringComparison.Ordinal);
        Assert.Contains("prefer existing helpers", brief, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, brief, StringComparison.Ordinal);
        Assert.DoesNotContain("layout-only", brief, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DysonBeginBuildPlanFlow.BuildInstruction(".dyson/plans/x.md"), brief, StringComparison.Ordinal);

        var stored = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
        Assert.False(stored.IsError);
        Assert.Equal(DysonPlanStatus.Building, stored.Value.Status);
        Assert.Equal(spawned.PersistenceId, stored.Value.BuildAgentId);
        Assert.NotNull(stored.Value.BuildAgentId);

        var second = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "2",
            ToolName = "BeginBuildPlan",
            Stage = 0,
            ArgumentsJson = $$"""{"planId":{{planId}}}""",
        });
        Assert.True(second.IsError);
        Assert.Contains($"agent #{spawned.Id}", second.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentId_reuses_existing_drone_instead_of_spawning()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var planId = await fixture.CreatePlanAsync("Reuse");

        using var http = new HttpClient();
        var parent = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        drone.SetPersistenceIdForTest(Guid.NewGuid());
        parent.RegisterForTest(drone);

        var executor = await fixture.ExecutorAsync(parent, http);
        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "BeginBuildPlan",
            Stage = 0,
            ArgumentsJson = $$"""{"planId":{{planId}},"agentId":{{drone.Id}}}""",
        });
        Assert.False(result.IsError, result.Content);
        Assert.Null(parent.LastSpawned);

        using var doc = JsonDocument.Parse(result.Content);
        Assert.Equal(drone.Id, doc.RootElement.GetProperty("agentId").GetInt32());
        Assert.Equal(drone.PersistenceId, doc.RootElement.GetProperty("persistenceId").GetGuid());

        var stored = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
        Assert.False(stored.IsError);
        Assert.Equal(DysonPlanStatus.Building, stored.Value.Status);
        Assert.Equal(drone.PersistenceId, stored.Value.BuildAgentId);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!drone.PromptInstructions.Any(i => i.Contains("ReadMetaPlan", StringComparison.Ordinal))
               && DateTime.UtcNow < deadline)
            await Task.Delay(15);

        Assert.Contains(drone.PromptInstructions, i => i.Contains("ReadMetaPlan", StringComparison.Ordinal));
        Assert.Contains(drone.PromptInstructions, i => i.Contains(planId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(drone.PromptInstructions, i => i.Contains("layout-only", StringComparison.OrdinalIgnoreCase));
    }
}

using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// SubmitMetaPlan creates/revises a MetaPlan row (drone-only, not a report) and round-trips via ReadMetaPlan.
/// </summary>
public class DysonSubmitMetaPlanTests
{
    [Fact]
    public async Task Submit_round_trips_through_ReadMetaPlan_and_revises_in_place()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), "dyson-submit-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        try
        {
            using var http = new HttpClient();
            var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(
                drone,
                workRoot,
                http,
                workDirectoryId: fixture.WorkDirectoryId,
                plans: fixture.Plans);

            var created = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "1",
                ToolName = "SubmitMetaPlan",
                Stage = 0,
                ArgumentsJson = """{"title":"First","markdown":"# v1 body"}""",
            });
            Assert.False(created.IsError, created.Content);
            Assert.False(created.EndsCurrentTurn);
            using (var doc = JsonDocument.Parse(created.Content))
            {
                Assert.Equal("First", doc.RootElement.GetProperty("title").GetString());
                Assert.Equal("draft", doc.RootElement.GetProperty("status").GetString());
            }

            var planId = JsonDocument.Parse(created.Content).RootElement.GetProperty("planId").GetInt64();
            Assert.True(planId > 0);

            var read = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "2",
                ToolName = "ReadMetaPlan",
                Stage = 0,
                ArgumentsJson = $$"""{"planId":{{planId}}}""",
            });
            Assert.False(read.IsError, read.Content);
            using (var doc = JsonDocument.Parse(read.Content))
            {
                Assert.Equal(planId, doc.RootElement.GetProperty("planId").GetInt64());
                Assert.Equal("First", doc.RootElement.GetProperty("title").GetString());
                Assert.Equal("# v1 body", doc.RootElement.GetProperty("markdown").GetString());
                Assert.Equal("draft", doc.RootElement.GetProperty("status").GetString());
            }

            var revised = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "3",
                ToolName = "SubmitMetaPlan",
                Stage = 0,
                ArgumentsJson = $$"""{"title":"Second","markdown":"# v2 body","planId":{{planId}}}""",
            });
            Assert.False(revised.IsError, revised.Content);
            using (var doc = JsonDocument.Parse(revised.Content))
            {
                Assert.Equal(planId, doc.RootElement.GetProperty("planId").GetInt64());
                Assert.Equal("Second", doc.RootElement.GetProperty("title").GetString());
            }

            var reread = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "4",
                ToolName = "ReadMetaPlan",
                Stage = 0,
                ArgumentsJson = $$"""{"planId":{{planId}}}""",
            });
            Assert.False(reread.IsError, reread.Content);
            using (var doc = JsonDocument.Parse(reread.Content))
            {
                Assert.Equal("# v2 body", doc.RootElement.GetProperty("markdown").GetString());
                Assert.Equal("Second", doc.RootElement.GetProperty("title").GetString());
            }

            var listed = await fixture.Plans.ListAsync(fixture.WorkDirectoryId);
            Assert.False(listed.IsError);
            Assert.Single(listed.Value);
            Assert.False(Directory.Exists(Path.Combine(workRoot, ".dyson", "plans")));
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Revise_planId_from_another_work_directory_is_not_found()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var planId = await fixture.CreatePlanAsync("Other wd");

        using var http = new HttpClient();
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        var executor = await fixture.ExecutorAsync(drone, http, fixture.OtherWorkDirectoryId);

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "SubmitMetaPlan",
            Stage = 0,
            ArgumentsJson = $$"""{"title":"Hijack","markdown":"# stolen","planId":{{planId}}}""",
        });
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);

        var original = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
        Assert.False(original.IsError);
        Assert.Equal("Other wd", original.Value.Title);
    }

    [Fact]
    public async Task Rejected_outside_meta_agent_drone_mode()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        DysonPlanToolStubSession.GrantTool(meta, "SubmitMetaPlan", DysonAgentModes.MetaAgentDrone);
        var executor = await fixture.ExecutorAsync(meta, http);

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "SubmitMetaPlan",
            Stage = 0,
            ArgumentsJson = """{"title":"Nope","markdown":"# body"}""",
        });
        Assert.True(result.IsError);
        Assert.Contains("only available in Meta Agent Drone mode", result.Content, StringComparison.Ordinal);
    }
}

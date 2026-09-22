using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ListPlans projection (no path, no markdown), SetPlanStatus parsing, DeletePlan Building guard,
/// and cross-work-directory not-found for Set/Delete.
/// </summary>
public class DysonPlanStatusTests
{
    [Fact]
    public async Task ListPlans_serialized_json_omits_path_and_markdown()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var secretBody = "# SECRET_MARKDOWN_BODY";
        await fixture.CreatePlanAsync("Listed", secretBody);

        using var http = new HttpClient();
        var session = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var executor = await fixture.ExecutorAsync(session, http);
        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "ListPlans",
            Stage = 0,
            ArgumentsJson = "{}",
        });

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("planRelativePath", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("markdown", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretBody, result.Content, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(result.Content);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var item = Assert.Single(doc.RootElement.EnumerateArray());
        var names = item.EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["buildAgentId", "planId", "status", "title", "updatedUtc"], names);
        Assert.Equal("Listed", item.GetProperty("title").GetString());
        Assert.Equal("draft", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SetPlanStatus_parses_accepted_values_and_rejects_draft_and_unknown()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var planId = await fixture.CreatePlanAsync();

        using var http = new HttpClient();
        var session = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var executor = await fixture.ExecutorAsync(session, http);

        var completed = await executor.ExecuteAsync(Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"Completed","note":"done"}"""));
        Assert.False(completed.IsError, completed.Content);
        var after = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
        Assert.False(after.IsError);
        Assert.Equal(DysonPlanStatus.Completed, after.Value.Status);
        Assert.Equal("done", after.Value.Note);

        var draft = await executor.ExecuteAsync(Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"draft"}"""));
        Assert.True(draft.IsError);
        Assert.Equal(DysonMetaAgentTools.SetPlanStatusAcceptedMessage, draft.Content);

        var unknown = await executor.ExecuteAsync(Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"banana"}"""));
        Assert.True(unknown.IsError);
        Assert.Equal(DysonMetaAgentTools.SetPlanStatusAcceptedMessage, unknown.Content);
    }

    [Fact]
    public async Task DeletePlan_refuses_building_and_deletes_otherwise()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var buildingId = await fixture.CreatePlanAsync(status: DysonPlanStatus.Building);
        var draftId = await fixture.CreatePlanAsync("Deletable");

        using var http = new HttpClient();
        var session = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var executor = await fixture.ExecutorAsync(session, http);

        var refused = await executor.ExecuteAsync(
            Call("DeletePlan", $$"""{"planId":{{buildingId}},"reason":"abandoned"}"""));
        Assert.True(refused.IsError);
        Assert.Contains("Building", refused.Content, StringComparison.Ordinal);
        Assert.Contains("SetPlanStatus", refused.Content, StringComparison.Ordinal);

        var stillThere = await fixture.Plans.GetAsync(buildingId, fixture.WorkDirectoryId);
        Assert.False(stillThere.IsError);

        var deleted = await executor.ExecuteAsync(
            Call("DeletePlan", $$"""{"planId":{{draftId}},"reason":"superseded"}"""));
        Assert.False(deleted.IsError, deleted.Content);
        var gone = await fixture.Plans.GetAsync(draftId, fixture.WorkDirectoryId);
        Assert.True(gone.IsError);
        Assert.Contains("not found", gone.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetPlanStatus_and_DeletePlan_do_not_cross_work_directories()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var planId = await fixture.CreatePlanAsync("Scoped");

        using var http = new HttpClient();
        var session = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var otherExecutor = await fixture.ExecutorAsync(session, http, fixture.OtherWorkDirectoryId);

        var set = await otherExecutor.ExecuteAsync(
            Call("SetPlanStatus", $$"""{"planId":{{planId}},"status":"stale"}"""));
        Assert.True(set.IsError);
        Assert.Contains("not found", set.Content, StringComparison.OrdinalIgnoreCase);

        var delete = await otherExecutor.ExecuteAsync(
            Call("DeletePlan", $$"""{"planId":{{planId}},"reason":"hijack"}"""));
        Assert.True(delete.IsError);
        Assert.Contains("not found", delete.Content, StringComparison.OrdinalIgnoreCase);

        var still = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
        Assert.False(still.IsError);
        Assert.Equal("Scoped", still.Value.Title);
        Assert.Equal(DysonPlanStatus.Draft, still.Value.Status);
    }

    private static DysonToolCall Call(string name, string args) => new()
    {
        CallId = "1",
        ToolName = name,
        Stage = 0,
        ArgumentsJson = args,
    };
}

using System.Text.Json;

using DysonHarness;

using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>
/// EditMetaPlan: WriteFile-shaped, atomic, per-plan serialised edits to an existing plan row, plus the
/// catalog / prompt awareness that makes the tool known to Meta Agent sessions.
/// </summary>
public class DysonEditMetaPlanTests
{
    private const string Body = "# Plan\n\n## Goal\nship it\n\n## Steps\n1. alpha\n2. beta\n";

    [Fact]
    public async Task Single_edit_replaces_in_place_without_a_new_row_and_publishes_once()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var bus = new DysonMessageBus();
        var hits = new List<DysonPlansChangedEvent>();
        var subscribed = bus.Subscribe<DysonPlansChangedEvent>(
            DysonBusScopes.WorkDirectory(fixture.WorkDirectoryId),
            hits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var subscription = subscribed.Value;

        using var http = new HttpClient();
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        drone.Config.Bus = bus;
        var executor = await fixture.ExecutorAsync(drone, http);
        var planId = await fixture.CreatePlanAsync("T", Body, status: DysonPlanStatus.Building);
        hits.Clear();

        var result = await Run(executor, "EditMetaPlan", Args(new { planId, old_text = "alpha", new_text = "ALPHA" }));

        Assert.False(result.IsError, result.Content);
        using (var doc = JsonDocument.Parse(result.Content))
        {
            var root = doc.RootElement;
            Assert.Equal(planId, root.GetProperty("planId").GetInt64());
            Assert.Equal("building", root.GetProperty("status").GetString());
            Assert.Equal(1, root.GetProperty("edits")[0].GetProperty("replacements").GetInt32());
            Assert.Equal(Body.Replace("alpha", "ALPHA").Length, root.GetProperty("chars").GetInt32());
            Assert.Equal(9, root.GetProperty("lines").GetInt32());
            Assert.True(root.TryGetProperty("updatedUtc", out _));
        }

        Assert.Equal(Body.Replace("alpha", "ALPHA"), await BodyAsync(fixture, planId));
        var listed = await fixture.Plans.ListAsync(fixture.WorkDirectoryId);
        Assert.Single(listed.Value);
        var evt = Assert.Single(hits);
        Assert.Equal(planId, evt.PlanId);
    }

    [Fact]
    public async Task Edits_apply_in_order_to_the_evolving_text()
    {
        var (fixture, executor, planId) = await SeedAsync("one two");
        await using var _ = fixture;

        var result = await Run(executor, "EditMetaPlan", Args(new
        {
            planId,
            edits = new[]
            {
                new { old_text = "one", new_text = "uno", replace_all = false },
                new { old_text = "uno two", new_text = "UNO TWO", replace_all = false },
            },
        }));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("UNO TWO", await BodyAsync(fixture, planId));
    }

    [Fact]
    public async Task Replace_all_replaces_every_occurrence_and_reports_the_count()
    {
        var (fixture, executor, planId) = await SeedAsync("x a x b x");
        await using var _ = fixture;

        var result = await Run(executor, "EditMetaPlan", Args(new { planId, old_text = "x", new_text = "y", replace_all = true }));

        Assert.False(result.IsError, result.Content);
        using var doc = JsonDocument.Parse(result.Content);
        Assert.Equal(3, doc.RootElement.GetProperty("edits")[0].GetProperty("replacements").GetInt32());
        Assert.Equal("y a y b y", await BodyAsync(fixture, planId));
    }

    [Fact]
    public async Task Ambiguous_match_names_the_edit_index_and_count_and_changes_nothing()
    {
        var (fixture, executor, planId) = await SeedAsync("dup\nmid\ndup\n");
        await using var _ = fixture;

        var result = await Run(executor, "EditMetaPlan", Args(new
        {
            planId,
            edits = new[]
            {
                new { old_text = "mid", new_text = "MID", replace_all = false },
                new { old_text = "dup", new_text = "DUP", replace_all = false },
            },
        }));

        Assert.True(result.IsError);
        Assert.Contains("edits[1]", result.Content, StringComparison.Ordinal);
        Assert.Contains("matched 2 times", result.Content, StringComparison.Ordinal);
        Assert.Contains("(2 matches)", result.Content, StringComparison.Ordinal);
        Assert.Equal("dup\nmid\ndup\n", await BodyAsync(fixture, planId));
    }

    [Fact]
    public async Task Not_found_names_the_edit_index()
    {
        var (fixture, executor, planId) = await SeedAsync("alpha beta");
        await using var _ = fixture;

        var result = await Run(executor, "EditMetaPlan", Args(new
        {
            planId,
            edits = new[]
            {
                new { old_text = "alpha", new_text = "A", replace_all = false },
                new { old_text = "missing", new_text = "M", replace_all = false },
            },
        }));

        Assert.True(result.IsError);
        Assert.Contains("edits[1]", result.Content, StringComparison.Ordinal);
        Assert.Contains("old_text not found", result.Content, StringComparison.Ordinal);
        Assert.Contains("(0 matches)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_batch_is_atomic_and_leaves_body_and_timestamp_untouched()
    {
        var (fixture, executor, planId) = await SeedAsync("alpha beta");
        await using var _ = fixture;
        var before = (await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId)).Value;

        var result = await Run(executor, "EditMetaPlan", Args(new
        {
            planId,
            title = "Renamed",
            edits = new[]
            {
                new { old_text = "alpha", new_text = "A", replace_all = false },
                new { old_text = "nope", new_text = "N", replace_all = false },
            },
        }));

        Assert.True(result.IsError);
        var after = (await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId)).Value;
        Assert.Equal("alpha beta", after.Markdown);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
    }

    [Fact]
    public async Task Content_replaces_the_whole_body()
    {
        var (fixture, executor, planId) = await SeedAsync(Body);
        await using var _ = fixture;

        var result = await Run(executor, "EditMetaPlan", Args(new { planId, content = "# New\nbody" }));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("# New\nbody", await BodyAsync(fixture, planId));
    }

    [Fact]
    public async Task Title_and_summary_revise_metadata_and_keep_the_body()
    {
        var (fixture, executor, planId) = await SeedAsync(Body);
        await using var _ = fixture;

        var result = await Run(executor, "EditMetaPlan", Args(new { planId, title = "Rev 7", summary = "renamed" }));

        Assert.False(result.IsError, result.Content);
        var plan = (await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId)).Value;
        Assert.Equal("Rev 7", plan.Title);
        Assert.Equal(Body, plan.Markdown);
    }

    [Fact]
    public async Task Mixed_modes_are_rejected_with_a_clear_error()
    {
        var (fixture, executor, planId) = await SeedAsync("alpha");
        await using var _ = fixture;

        var contentAndOld = await Run(executor, "EditMetaPlan", Args(new { planId, content = "x", old_text = "alpha", new_text = "b" }));
        var oldAndEdits = await Run(executor, "EditMetaPlan", Args(new
        {
            planId,
            old_text = "alpha",
            new_text = "b",
            edits = new[] { new { old_text = "alpha", new_text = "c", replace_all = false } },
        }));
        var none = await Run(executor, "EditMetaPlan", Args(new { planId }));

        foreach (var rejected in new[] { contentAndOld, oldAndEdits })
        {
            Assert.True(rejected.IsError);
            Assert.Contains("exactly one of", rejected.Content, StringComparison.Ordinal);
        }

        Assert.True(none.IsError);
        Assert.Equal("alpha", await BodyAsync(fixture, planId));
    }

    [Fact]
    public async Task Unknown_plan_id_is_not_found()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), http);

        var result = await Run(executor, "EditMetaPlan", Args(new { planId = 9999, old_text = "a", new_text = "b" }));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plan_from_another_work_directory_is_rejected_and_untouched()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        var planId = await fixture.CreatePlanAsync("Other", "alpha");
        using var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(
            new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone),
            http,
            fixture.OtherWorkDirectoryId);

        var result = await Run(executor, "EditMetaPlan", Args(new { planId, old_text = "alpha", new_text = "stolen" }));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("alpha", await BodyAsync(fixture, planId));
    }

    [Fact]
    public async Task Concurrent_edits_to_one_plan_all_land()
    {
        const int count = 12;
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var planId = await fixture.CreatePlanAsync("Race", string.Join("\n", Enumerable.Range(0, count).Select(i => $"line-{i}-old")));

        var tasks = new List<Task<DysonToolCallResult>>();
        for (var i = 0; i < count; i++)
        {
            var mode = i % 2 == 0 ? DysonAgentModes.MetaAgentDrone : DysonAgentModes.MetaAgent;
            var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(mode), http);
            tasks.Add(Run(executor, "EditMetaPlan", Args(new { planId, old_text = $"line-{i}-old", new_text = $"line-{i}-new" })));
        }

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.False(r.IsError, r.Content));
        var body = await BodyAsync(fixture, planId);
        for (var i = 0; i < count; i++)
        {
            Assert.Contains($"line-{i}-new", body, StringComparison.Ordinal);
            Assert.DoesNotContain($"line-{i}-old", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Root_meta_agent_can_read_and_edit_a_plan()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var meta = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        var executor = await fixture.ExecutorAsync(meta, http);
        var planId = await fixture.CreatePlanAsync("Root", "alpha");

        var read = await Run(executor, "ReadMetaPlan", Args(new { planId }));
        Assert.False(read.IsError, read.Content);
        Assert.Equal("alpha", JsonDocument.Parse(read.Content).RootElement.GetProperty("markdown").GetString());

        var edit = await Run(executor, "EditMetaPlan", Args(new { planId, old_text = "alpha", new_text = "beta" }));
        Assert.False(edit.IsError, edit.Content);
        Assert.Equal("beta", await BodyAsync(fixture, planId));
    }

    [Fact]
    public void Tool_is_registered_for_meta_agent_and_drone_and_absent_elsewhere()
    {
        foreach (var mode in new[] { DysonAgentModes.MetaAgent, DysonAgentModes.MetaAgentDrone })
        {
            var tools = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), mode).Tools;
            Assert.True(tools.ContainsKey("EditMetaPlan"), mode);
            Assert.True(tools.ContainsKey("ReadMetaPlan"), mode);
        }

        foreach (var mode in new[] { DysonAgentModes.Explore, DysonAgentModes.Work })
        {
            var tools = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), mode).Tools;
            Assert.False(tools.ContainsKey("EditMetaPlan"), mode);
        }

        var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent).Tools;
        Assert.False(meta.ContainsKey("SubmitMetaPlan"));
        using var schema = JsonDocument.Parse(meta["EditMetaPlan"].InputSchemaJson);
        var props = schema.RootElement.GetProperty("properties");
        foreach (var name in new[] { "planId", "content", "old_text", "new_text", "replace_all", "edits", "title", "summary" })
            Assert.True(props.TryGetProperty(name, out _), name);
        Assert.False(props.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task Invalid_json_from_SubmitMetaPlan_names_the_reason_the_cut_off_and_EditMetaPlan()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), http);
        var cutOff = "{\"planId\":3,\"title\":\"T\",\"markdown\":\"# half a plan with \\\"quotes\\\" and";

        var result = await Run(executor, "SubmitMetaPlan", cutOff);

        Assert.True(result.IsError);
        Assert.Contains("invalid JSON arguments", result.Content, StringComparison.Ordinal);
        Assert.Contains($"{cutOff.Length} chars received", result.Content, StringComparison.Ordinal);
        Assert.Contains("cut off", result.Content, StringComparison.Ordinal);
        Assert.Contains("EditMetaPlan", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompts_briefs_and_descriptions_name_EditMetaPlan()
    {
        var meta = DysonAgentSystemPrompts.ForMode(DysonAgentModes.MetaAgent).Value;
        Assert.Contains("EditMetaPlan", meta, StringComparison.Ordinal);
        Assert.Contains("ReadMetaPlan", meta, StringComparison.Ordinal);
        Assert.Contains("one EditMetaPlan call per plan per stage", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("you never read the plan body", meta, StringComparison.Ordinal);

        var drone = DysonAgentSystemPrompts.ForMode(DysonAgentModes.MetaAgentDrone).Value;
        Assert.Contains("EditMetaPlan", drone, StringComparison.Ordinal);

        Assert.Contains("EditMetaPlan", DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate, StringComparison.Ordinal);
        Assert.Contains("EditMetaPlan", DysonMetaBuildBrief.Build(7, "Plan", null), StringComparison.Ordinal);
        Assert.Contains(
            "EditMetaPlan",
            DysonFileViewerComments.FormatPrompt("metaplan:7/plan.md", [("excerpt", "text")]),
            StringComparison.Ordinal);

        var droneTools = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgentDrone).Tools;
        var submit = droneTools["SubmitMetaPlan"].Description;
        Assert.Contains("EditMetaPlan", submit, StringComparison.Ordinal);
        Assert.Contains("invalid JSON", submit, StringComparison.Ordinal);
        var edit = droneTools["EditMetaPlan"].Description;
        Assert.Contains("at most one EditMetaPlan call per plan per stage", edit, StringComparison.Ordinal);
        Assert.Contains("edits[]", edit, StringComparison.Ordinal);
        Assert.Contains("anchor", edit, StringComparison.Ordinal);
    }

    private static async Task<(DysonPlanToolFixture Fixture, DysonWorkspaceToolExecutor Executor, long PlanId)> SeedAsync(string markdown)
    {
        var fixture = await DysonPlanToolFixture.CreateAsync();
        var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), http);
        var planId = await fixture.CreatePlanAsync("T", markdown);
        return (fixture, executor, planId);
    }

    private static async Task<string?> BodyAsync(DysonPlanToolFixture fixture, long planId) =>
        (await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId)).Value.Markdown;

    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static Task<DysonToolCallResult> Run(DysonWorkspaceToolExecutor executor, string tool, string args) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Stage = 0,
            ArgumentsJson = args,
        });
}

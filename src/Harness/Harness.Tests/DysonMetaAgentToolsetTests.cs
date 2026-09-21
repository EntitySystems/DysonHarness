using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Meta Agent catalog is the allowlist; Meta Agent Drone drops the FromParent trio.</summary>
public class DysonMetaAgentToolsetTests
{
    [Fact]
    public void Meta_agent_catalog_is_exactly_the_allowlist()
    {
        var pipeline = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        var names = pipeline.Tools.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = DysonMetaAgentTools.AllowedToolNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, names);
    }

    [Fact]
    public void Meta_agent_drone_catalog_strips_from_parent_trio_and_adds_plan_tools()
    {
        var pipeline = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.MetaAgentDrone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);

        Assert.False(pipeline.Tools.ContainsKey("AskQuestionFromParent"));
        Assert.False(pipeline.Tools.ContainsKey("PromptUserDialogFromParent"));
        Assert.False(pipeline.Tools.ContainsKey("TriggerParentEvent"));
        Assert.True(pipeline.Tools.ContainsKey("ReadMetaPlan"));
        Assert.True(pipeline.Tools.ContainsKey("SubmitMetaPlan"));
        Assert.True(pipeline.Tools.ContainsKey("ReadFile"));
        Assert.True(pipeline.Tools.ContainsKey("SubmitSubagentReport"));
    }

    [Fact]
    public void Child_roster_with_reports_is_byte_identical_across_calls()
    {
        var parent = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
        drone.WorktreeBranch = "dyson/abcd1234";
        parent.RegisterForTest(drone);

        var first = parent.FormatChildRosterJson(includeReports: true);
        var second = parent.FormatChildRosterJson(includeReports: true);
        Assert.Equal(first, second);

        using var doc = JsonDocument.Parse(first);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        var item = doc.RootElement[0];
        Assert.Equal("drone", item.GetProperty("kind").GetString());
        Assert.Equal("dyson/abcd1234", item.GetProperty("worktreeBranch").GetString());
        Assert.True(item.TryGetProperty("lastReport", out _));
        Assert.True(item.TryGetProperty("finishedAt", out _));
        Assert.False(item.TryGetProperty("notice", out _));
    }

    [Fact]
    public void List_subagents_json_does_not_grow_report_fields()
    {
        var parent = new StubSession(DysonAgentModes.Work);
        var child = new StubSession(DysonAgentModes.Explore);
        parent.RegisterForTest(child);

        using var doc = JsonDocument.Parse(parent.FormatListSubagentsJson());
        var item = doc.RootElement[0];
        Assert.False(item.TryGetProperty("kind", out _));
        Assert.False(item.TryGetProperty("lastReport", out _));
        Assert.False(item.TryGetProperty("finishedAt", out _));
        Assert.False(item.TryGetProperty("worktreeBranch", out _));
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

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

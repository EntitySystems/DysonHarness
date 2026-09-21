using DysonHarness;

namespace Harness.Tests;

/// <summary>DeleteMetaAgent: terminal-only, child-of-this-session only.</summary>
public class DysonDeleteMetaAgentTests
{
    [Fact]
    public async Task Rejects_non_terminal_and_not_my_child()
    {
        using var http = new HttpClient();
        var parent = new StubSession(DysonAgentModes.MetaAgent);
        var child = new StubSession(DysonAgentModes.MetaAgentDrone);
        parent.RegisterForTest(child);

        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(parent, Path.GetTempPath(), http);

        var running = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "DeleteMetaAgent",
            Stage = 0,
            ArgumentsJson = $$"""{"agentId":{{child.Id}},"reason":"prune"}""",
        });
        Assert.True(running.IsError);
        Assert.Contains($"Agent #{child.Id} is still {child.Status}. Stop it first.", running.Content, StringComparison.Ordinal);

        var unknown = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "2",
            ToolName = "DeleteMetaAgent",
            Stage = 0,
            ArgumentsJson = """{"agentId":99,"reason":"prune"}""",
        });
        Assert.True(unknown.IsError);
        Assert.Contains("Agent #99 is not a child of this session.", unknown.Content, StringComparison.Ordinal);

        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Completed, "done"));
        var ok = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "3",
            ToolName = "DeleteMetaAgent",
            Stage = 0,
            ArgumentsJson = $$"""{"agentId":{{child.Id}},"reason":"recorded"}""",
        });
        Assert.False(ok.IsError);
        Assert.False(parent.TryGetSubagent(child.Id, out _));
    }

    [Fact]
    public async Task Rejects_when_a_descendant_is_still_running()
    {
        using var http = new HttpClient();
        var parent = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
        var explore = new StubSession(DysonAgentModes.Explore);
        parent.RegisterForTest(drone);
        drone.RegisterForTest(explore);
        Assert.True(drone.TryMarkTerminal(DysonSessionStatus.Completed, "drone done"));

        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(parent, Path.GetTempPath(), http);
        var blocked = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "DeleteMetaAgent",
            Stage = 0,
            ArgumentsJson = $$"""{"agentId":{{drone.Id}},"reason":"prune"}""",
        });
        Assert.True(blocked.IsError);
        Assert.Contains($"Agent #{explore.Id} is still {explore.Status}. Stop it first.", blocked.Content, StringComparison.Ordinal);
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

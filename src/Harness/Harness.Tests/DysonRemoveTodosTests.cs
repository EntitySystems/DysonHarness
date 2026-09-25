using DysonHarness;

namespace Harness.Tests;

/// <summary>RemoveTodos is Meta-Agent-only (runtime mode gate, same shape as SubmitPlan).</summary>
public class DysonRemoveTodosTests
{
    [Fact]
    public async Task Removes_by_task_code_in_meta_agent_and_rejects_outside_that_mode()
    {
        using var http = new HttpClient();
        var meta = new StubSession(DysonAgentModes.MetaAgent);
        var created = await meta.CreateTodoAsync("gone", "Will remove");
        Assert.False(created.IsError);
        var keep = await meta.CreateTodoAsync("keep", "Stays");
        Assert.False(keep.IsError);

        var metaExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(meta, Path.GetTempPath(), http);
        var removed = await metaExecutor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "RemoveTodos",
            Stage = 0,
            ArgumentsJson = """{"taskCodes":["gone"],"reason":"abandoned"}""",
        });
        Assert.False(removed.IsError);
        Assert.DoesNotContain(meta.Todos, t => t.TaskCode == "gone");
        Assert.Contains(meta.Todos, t => t.TaskCode == "keep");

        var work = new StubSession(DysonAgentModes.Work);
        work.McpPipeline.Tools["RemoveTodos"] = DysonSessionToolsetBuilder
            .Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent)
            .Tools["RemoveTodos"];
        var workExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, Path.GetTempPath(), http);
        var rejected = await workExecutor.ExecuteAsync(new DysonToolCall
        {
            CallId = "2",
            ToolName = "RemoveTodos",
            Stage = 0,
            ArgumentsJson = """{"taskCodes":["gone"],"reason":"abandoned"}""",
        });
        Assert.True(rejected.IsError);
        Assert.Contains("only available in Meta Agent mode", rejected.Content, StringComparison.Ordinal);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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

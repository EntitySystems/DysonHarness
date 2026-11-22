using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// StopSubagentAsync recursive flag. Omitted (the three-arg overload) does not recurse;
/// the tool parser treats a missing argument as true and calls the bool overload.
/// </summary>
public class DysonStopSubagentRecursiveTests
{
    [Fact]
    public async Task Omitting_recursive_stops_only_the_named_agent()
    {
        var parent = new Parent();
        var child = new Child();
        var grandchild = new Child();
        parent.RegisterForTest(child);
        child.RegisterForTest(grandchild);

        var stopped = await parent.StopSubagentAsync(child.Id, "omit");

        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Equal(DysonSessionStatus.Active, grandchild.Status);
    }

    [Fact]
    public async Task Explicit_true_stops_the_target_and_its_descendants()
    {
        var parent = new Parent();
        var child = new Child();
        var grandchild = new Child();
        parent.RegisterForTest(child);
        child.RegisterForTest(grandchild);

        var stopped = await parent.StopSubagentAsync(child.Id, "recurse", recursive: true);

        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Equal(DysonSessionStatus.Stopped, grandchild.Status);
    }

    [Fact]
    public async Task Explicit_false_leaves_children_running()
    {
        var parent = new Parent();
        var child = new Child();
        var grandchild = new Child();
        parent.RegisterForTest(child);
        child.RegisterForTest(grandchild);

        var stopped = await parent.StopSubagentAsync(child.Id, "only me", recursive: false);

        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Equal(DysonSessionStatus.Active, grandchild.Status);
    }

    [Fact]
    public async Task Recursive_true_stops_a_grandchild_not_only_direct_children()
    {
        var parent = new Parent();
        var child = new Child();
        var grandchild = new Child();
        var great = new Child();
        parent.RegisterForTest(child);
        child.RegisterForTest(grandchild);
        grandchild.RegisterForTest(great);

        var stopped = await parent.StopSubagentAsync(child.Id, "deep", recursive: true);

        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Equal(DysonSessionStatus.Stopped, grandchild.Status);
        Assert.Equal(DysonSessionStatus.Stopped, great.Status);
    }

    [Fact]
    public async Task Stopping_an_agent_with_no_children_succeeds()
    {
        var parent = new Parent();
        var child = new Child();
        parent.RegisterForTest(child);

        var stopped = await parent.StopSubagentAsync(child.Id, "leaf", recursive: true);

        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Empty(child.SubSessions);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class Parent() : DysonAgentSession(
        DysonAgentModes.Work,
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
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Child() : DysonAgentSession(
        DysonAgentModes.Explore,
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
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

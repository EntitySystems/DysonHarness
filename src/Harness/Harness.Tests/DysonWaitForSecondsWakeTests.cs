using DysonHarness;

namespace Harness.Tests;

/// <summary>Plan 365: a Meta Agent Drone's WaitForSeconds ends early on a child parent event.</summary>
public class DysonWaitForSecondsWakeTests
{
    [Fact]
    public async Task Drone_wait_wakes_on_child_event()
    {
        var (_, drone, worker) = BuildTree();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(drone, Path.GetTempPath(), http);

        var wait = executor.ExecuteAsync(Wait(30));
        await Task.Delay(50);
        var trigger = worker.TriggerParentEventAsync("message", "q");

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsError, result.Content);
        Assert.Contains("\"status\":\"interrupted\"", result.Content, StringComparison.Ordinal);
        var evt = Assert.Single(drone.PendingOrRecentParentEvents);
        Assert.Contains(evt.EventId.ToString("D"), result.Content, StringComparison.OrdinalIgnoreCase);

        Assert.True(drone.RespondToSubagentEvent(worker.Id, evt.EventId, "a").IsSuccess);
        var reply = await trigger.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("a", reply.Value);
    }

    [Fact]
    public async Task Pending_event_wakes_once()
    {
        var (_, drone, worker) = BuildTree();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(drone, Path.GetTempPath(), http);
        var trigger = worker.TriggerParentEventAsync("message", "q");

        var first = await executor.ExecuteAsync(Wait(30)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"status\":\"interrupted\"", first.Content, StringComparison.Ordinal);

        var second = await executor.ExecuteAsync(Wait(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"status\":\"ok\"", second.Content, StringComparison.Ordinal);

        var evt = Assert.Single(drone.PendingOrRecentParentEvents);
        Assert.True(drone.RespondToSubagentEvent(worker.Id, evt.EventId, "a").IsSuccess);
        await trigger.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Work_parent_wait_ignores_child_event()
    {
        var work = new StubSession(DysonAgentModes.Work);
        var child = new StubSession(DysonAgentModes.Work);
        work.RegisterForTest(child);
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, Path.GetTempPath(), http);
        var trigger = child.TriggerParentEventAsync("message", "q");

        var result = await executor.ExecuteAsync(Wait(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"status\":\"ok\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"waitedSeconds\":1", result.Content, StringComparison.Ordinal);

        var evt = Assert.Single(work.PendingOrRecentParentEvents);
        work.RespondToSubagentEvent(child.Id, evt.EventId, "a");
        await trigger.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Drone_awaiting_own_parent_does_not_wake()
    {
        var (root, drone, worker) = BuildTree();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(drone, Path.GetTempPath(), http);
        var own = drone.TriggerParentEventAsync("message", "to root");
        var trigger = worker.TriggerParentEventAsync("message", "q");

        var result = await executor.ExecuteAsync(Wait(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"status\":\"ok\"", result.Content, StringComparison.Ordinal);

        var rootEvt = Assert.Single(root.PendingOrRecentParentEvents);
        root.RespondToSubagentEvent(drone.Id, rootEvt.EventId, "r");
        await own.WaitAsync(TimeSpan.FromSeconds(5));
        var evt = Assert.Single(drone.PendingOrRecentParentEvents);
        drone.RespondToSubagentEvent(worker.Id, evt.EventId, "a");
        await trigger.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ShouldWakeForParentEvent_rules()
    {
        var id = Guid.NewGuid();
        var evt = new DysonParentEvent { EventId = id, SubagentId = 1, Kind = "message", Payload = "q" };
        Assert.True(DysonWorkspaceToolExecutor.ShouldWakeForParentEvent(evt, []));

        var answering = DysonAgentSession.CreateNormalTurn("- eventId: " + id.ToString("D"));
        Assert.False(DysonWorkspaceToolExecutor.ShouldWakeForParentEvent(evt, [answering]));
        Assert.True(DysonWorkspaceToolExecutor.ShouldWakeForParentEvent(
            evt, [DysonAgentSession.CreateNormalTurn("unrelated")]));

        evt.WokeWait = true;
        Assert.False(DysonWorkspaceToolExecutor.ShouldWakeForParentEvent(evt, []));

        var addressed = new DysonParentEvent
        {
            EventId = Guid.NewGuid(),
            SubagentId = 1,
            Kind = "message",
            Payload = "q",
            Status = DysonParentEventStatus.Addressed,
        };
        Assert.False(DysonWorkspaceToolExecutor.ShouldWakeForParentEvent(addressed, []));
    }

    [Fact]
    public async Task Cancel_still_aborts_wait()
    {
        using var http = new HttpClient();
        var work = new StubSession(DysonAgentModes.Work);
        var workExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, Path.GetTempPath(), http);
        var (_, drone, _) = BuildTree();
        var droneExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(drone, Path.GetTempPath(), http);

        var workResult = await CancelledWait(workExecutor);
        var droneResult = await CancelledWait(droneExecutor);
        Assert.True(workResult.IsError);
        Assert.Equal(workResult.IsError, droneResult.IsError);
        Assert.Equal(workResult.Content, droneResult.Content);
    }

    private static async Task<DysonToolCallResult> CancelledWait(DysonWorkspaceToolExecutor executor)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        return await executor.ExecuteAsync(Wait(30), cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static (StubSession Root, StubSession Drone, StubSession Worker) BuildTree()
    {
        var root = new StubSession(DysonAgentModes.MetaAgent);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
        var worker = new StubSession(DysonAgentModes.Drone);
        root.RegisterForTest(drone);
        drone.RegisterForTest(worker);
        return (root, drone, worker);
    }

    private static DysonToolCall Wait(int seconds) => new()
    {
        CallId = "w" + seconds,
        ToolName = "WaitForSeconds",
        Stage = 0,
        ArgumentsJson = $$"""{"seconds":{{seconds}}}""",
    };

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

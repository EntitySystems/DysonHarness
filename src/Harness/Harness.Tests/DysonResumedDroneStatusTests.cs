using System.Text.Json;
using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// After an application restart: a resumed Interrupted drone reads Active, Stop cancels its run,
/// and restart sweep / restore leave a Stopped drone's status and report unchanged.
/// </summary>
public class DysonResumedDroneStatusTests
{
    [Fact]
    public async Task Interrupted_child_resumed_by_parent_inject_reads_active()
    {
        var parent = new ParentSession();
        var child = new HangingChildSession();
        parent.RegisterForTest(child);
        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Interrupted, DysonChildReportWatch.ApplicationRestartReason));

        var injected = await parent.TriggerSubagentEventAsync(child.Id, "resume the task");
        Assert.True(injected.IsSuccess, injected.IsError ? injected.Error : null);
        using (var doc = JsonDocument.Parse(injected.Value))
            Assert.True(doc.RootElement.GetProperty("reopened").GetBoolean());

        await child.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DysonSessionStatus.Active, child.Status);
        Assert.True(child.HasActiveBackgroundRun);

        using (var roster = JsonDocument.Parse(await parent.FormatChildRosterJsonAsync(includeReports: true)))
        {
            var item = Assert.Single(roster.RootElement.EnumerateArray());
            Assert.Equal(nameof(DysonSessionStatus.Active), item.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("finishedAt").ValueKind);
        }

        child.CancelBackgroundRunForTest();
        await child.PromptFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Stop_on_resumed_child_cancels_its_run()
    {
        var parent = new ParentSession();
        var child = new HangingChildSession();
        parent.RegisterForTest(child);
        Assert.True(child.TryMarkTerminal(DysonSessionStatus.Interrupted, DysonChildReportWatch.ApplicationRestartReason));

        var injected = await parent.TriggerSubagentEventAsync(child.Id, "resume the task");
        Assert.True(injected.IsSuccess, injected.IsError ? injected.Error : null);
        await child.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopped = await parent.StopSubagentAsync(child.Id, "stop the resumed drone");
        Assert.True(stopped.IsSuccess, stopped.IsError ? stopped.Error : null);
        using (var doc = JsonDocument.Parse(stopped.Value))
            Assert.Equal(nameof(DysonSessionStatus.Stopped), doc.RootElement.GetProperty("status").GetString());

        await child.PromptFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.False(child.HasActiveBackgroundRun);
        Assert.Equal("stop the resumed drone", child.LastReportSummary);
    }

    [Fact]
    public async Task Inject_waiter_is_released_when_the_host_starts_the_queued_turn()
    {
        var parent = new ParentSession();
        var child = new HangingChildSession();
        parent.RegisterForTest(child);
        child.StartBackgroundPrompt();
        await child.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var inject = parent.TriggerSubagentEventAsync(child.Id, "next boundary", timeoutSeconds: 10);
        Assert.True(child.HasPendingTurn);

        // The host (not KickOff) takes the queued turn and starts it.
        Assert.True(child.TryDequeuePendingTurn(out var queued));
        child.AddTurnForTest(queued);
        using (child.BeginInFlightPrompt(queued))
        {
            var result = await inject.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
            using var doc = JsonDocument.Parse(result.Value);
            Assert.Equal("delivered", doc.RootElement.GetProperty("status").GetString());
        }

        child.CancelBackgroundRunForTest();
        await child.PromptFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RecoverAsync_leaves_stopped_child_status_and_report_unchanged()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var recovery = new DysonSessionRecoveryService(sessions);

        var rootId = await CreateSessionAsync(sessions, 0, "root", null, DysonSessionStatus.Active);
        var childId = await CreateSessionAsync(sessions, 1, "stopped drone", rootId, DysonSessionStatus.Stopped);
        var stopLog = DysonSessionLogPayload.CreateEntry(
            childId,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(DysonSessionStatus.Stopped, "stopped by user"));
        Assert.False((await sessions.AppendLogAsync(stopLog)).IsError);

        var recovered = await recovery.RecoverAsync();
        Assert.True(recovered.IsSuccess, recovered.IsError ? recovered.Error : null);
        Assert.Equal(0, recovered.Value.DescendantsInterrupted);

        var child = await sessions.GetFullSessionAsync(childId);
        Assert.True(child.IsSuccess, child.IsError ? child.Error : null);
        Assert.Equal(DysonSessionStatus.Stopped, child.Value.Session.Status);
        var status = Assert.Single(child.Value.Logs, l => l.Kind == nameof(DysonSessionLogKind.SessionStatusChanged));
        Assert.Equal(
            "stopped by user",
            DysonSessionLogPayload.Deserialize<DysonSessionLogSessionStatusChanged>(status.PayloadJson)?.Reason);

        var root = await sessions.GetFullSessionAsync(rootId);
        Assert.True(root.IsSuccess, root.IsError ? root.Error : null);
        Assert.DoesNotContain(root.Value.Logs, l => l.Kind == nameof(DysonSessionLogKind.Interrupt));
    }

    [Fact]
    public async Task Restored_stopped_child_is_not_re_reported_as_application_restart()
    {
        var parent = new ParentSession();
        var child = new HangingChildSession();
        var childId = Guid.NewGuid();
        child.RestoreForTest(new DysonPersistedSession
        {
            Session = new DysonSessionEntity
            {
                Id = childId,
                RuntimeId = 5,
                AgentMode = DysonAgentModes.MetaAgentDrone,
                Status = DysonSessionStatus.Stopped,
                Title = "stopped drone",
                SystemPromptSnapshot = "test",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow,
            },
            Turns = [],
            Logs =
            [
                DysonSessionLogPayload.CreateEntry(
                    childId,
                    DysonSessionLogKind.SessionStatusChanged,
                    new DysonSessionLogSessionStatusChanged(DysonSessionStatus.Stopped, "stopped by user"),
                    sequence: 1),
            ],
            Todos = [],
        });
        parent.RestoreRegisteredSubagent(child);

        await child.ApplyChildReportWatchAsync();

        Assert.False(parent.TryDequeueInterrupt(out _));
        Assert.Equal(DysonSessionStatus.Stopped, child.Status);
        Assert.Equal("stopped by user", child.LastReportSummary);
    }

    private static async Task<Guid> CreateSessionAsync(
        IDysonSessionRepository sessions,
        int runtimeId,
        string title,
        Guid? parentSessionId,
        DysonSessionStatus status)
    {
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = runtimeId,
            ParentSessionId = parentSessionId,
            AgentMode = parentSessionId is null ? DysonAgentModes.MetaAgent : DysonAgentModes.MetaAgentDrone,
            Title = title,
            SystemPromptSnapshot = title,
            Status = status,
        });
        Assert.False(created.IsError, created.IsError ? created.Error : null);
        return created.Value;
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class ParentSession() : DysonAgentSession(
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

    private sealed class HangingChildSession() : DysonAgentSession(
        DysonAgentModes.MetaAgentDrone,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public readonly TaskCompletionSource PromptStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource PromptFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RestoreForTest(DysonPersistedSession state) => RestoreFromPersisted(state);

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public void StartBackgroundPrompt()
        {
            var runCts = new CancellationTokenSource();
            AttachBackgroundRun(runCts);
            KickOffChildPrompt(
                this,
                new DysonAgentTurn
                {
                    Kind = DysonAgentTurnKind.Normal,
                    Instruction = "hold until cancelled",
                    StartedUtc = DateTime.UtcNow,
                },
                runCts);
        }

        public void CancelBackgroundRunForTest() => CancelBackgroundRun();

        public override async Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
        {
            PromptStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return VoidResult<string>.Success;
            }
            finally
            {
                PromptFinished.TrySetResult();
            }
        }

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

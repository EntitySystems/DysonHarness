using System.Text.Json;

using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// SubmitSubagentReport status "blocked": terminal like Failed, skips the incomplete-todo guard,
/// notifies the parent with SubagentBlocked, and is reopenable. Completed with open todos still errors.
/// </summary>
public class DysonBlockedReportTests
{
    [Fact]
    public async Task Blocked_with_pending_todos_succeeds()
    {
        var parent = new StubSession();
        var child = new StubSession(DysonAgentModes.Drone);
        parent.RegisterForTest(child);
        Assert.True((await child.CreateTodoAsync("t1", "One", DysonSessionTodoStatus.Pending)).IsSuccess);
        Assert.True((await child.CreateTodoAsync("t2", "Two", DysonSessionTodoStatus.Ongoing)).IsSuccess);

        var submitted = await child.SubmitSubagentReportAsync("waiting for Unity", blocked: true);
        Assert.True(submitted.IsSuccess, submitted.IsError ? submitted.Error : null);
        Assert.Equal(DysonSessionStatus.Blocked, child.Status);
        Assert.True(child.IsTerminal);
        Assert.Equal("waiting for Unity", child.LastReportSummary);

        Assert.True(parent.TryDequeueInterrupt(out var interrupt));
        Assert.Equal(DysonAgentInterruptKind.SubagentBlocked, interrupt.Kind);
        Assert.Equal(child.Id, interrupt.SubagentId);
        Assert.Equal("waiting for Unity", interrupt.Summary);
        Assert.Contains("outcome: blocked", DysonSubagentReportPrompt.FormatReportBlock(interrupt, "t"), StringComparison.Ordinal);
        Assert.False(parent.TryDequeueInterrupt(out _));
    }

    [Fact]
    public async Task Completed_with_pending_todos_still_fails()
    {
        var parent = new StubSession();
        var child = new StubSession(DysonAgentModes.Drone);
        parent.RegisterForTest(child);
        Assert.True((await child.CreateTodoAsync("t1", "One", DysonSessionTodoStatus.Pending)).IsSuccess);
        Assert.True((await child.CreateTodoAsync("t2", "Two", DysonSessionTodoStatus.Ongoing)).IsSuccess);

        var submitted = await child.SubmitSubagentReportAsync("done");
        Assert.True(submitted.IsError);
        Assert.StartsWith("SubmitSubagentReport: incomplete todos:", submitted.Error, StringComparison.Ordinal);
        Assert.Equal(DysonSessionStatus.Active, child.Status);
        Assert.False(parent.TryDequeueInterrupt(out _));
    }

    [Fact]
    public async Task Blocked_is_surfaced_to_parent()
    {
        var parent = new StubSession();
        var child = new StubSession(DysonAgentModes.Drone);
        parent.RegisterForTest(child);

        Assert.True((await child.SubmitSubagentReportAsync("need credential", blocked: true)).IsSuccess);

        var waited = await parent.WaitForSubagentAsync(child.Id, 5_000);
        Assert.True(waited.IsSuccess, waited.IsError ? waited.Error : null);
        using (var doc = JsonDocument.Parse(waited.Value))
        {
            Assert.Equal("Blocked", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("need credential", doc.RootElement.GetProperty("summary").GetString());
        }

        using (var doc = JsonDocument.Parse(await parent.FormatChildRosterJsonAsync(includeReports: true)))
        {
            var top = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal("Blocked", top.GetProperty("status").GetString());
            Assert.Equal("need credential", top.GetProperty("lastReport").GetString());
        }

        var retry = await child.SubmitSubagentReportAsync("again", blocked: true);
        Assert.True(retry.IsError);
        Assert.Contains("already submitted", retry.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DysonSessionStatus.Blocked, child.Status);
        Assert.Equal("need credential", child.LastReportSummary);

        Assert.True(child.TryReopenForNewParentTask());
        Assert.Equal(DysonSessionStatus.Active, child.Status);
    }

    [Fact]
    public async Task Blocked_executor_call_persists()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);
        var parentId = await CreateAsync(sessions, 0, null, DysonAgentModes.Work, "parent");
        var childId = await CreateAsync(sessions, 1, parentId, DysonAgentModes.Drone, "child");

        var parent = new StubSession(DysonAgentModes.Work, sessions, parentId);
        parent.ConfigureRootForTest();
        var child = new StubSession(DysonAgentModes.Drone, sessions, childId);
        parent.RegisterForTest(child);
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(child, Path.GetTempPath(), http, sessions);

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "b1",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = """{"summary":"x","status":"blocked"}""",
        });
        Assert.False(result.IsError, result.IsError ? result.Content : null);
        Assert.True(result.EndsCurrentTurn);
        Assert.Equal(DysonSessionStatus.Blocked, child.Status);

        var invalid = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "b2",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = """{"summary":"x","status":"paused"}""",
        });
        Assert.True(invalid.IsError);
        Assert.Contains("'blocked'", invalid.Content, StringComparison.Ordinal);

        var persistedChild = await sessions.GetFullSessionAsync(childId);
        Assert.True(persistedChild.IsSuccess, persistedChild.IsError ? persistedChild.Error : null);
        Assert.Equal(DysonSessionStatus.Blocked, persistedChild.Value.Session.Status);

        var persistedParent = await sessions.GetFullSessionAsync(parentId);
        Assert.True(persistedParent.IsSuccess, persistedParent.IsError ? persistedParent.Error : null);
        var interruptLog = Assert.Single(
            persistedParent.Value.Logs,
            l => l.Kind == nameof(DysonSessionLogKind.Interrupt));
        var payload = DysonSessionLogPayload.Deserialize<DysonSessionLogInterrupt>(interruptLog.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(nameof(DysonAgentInterruptKind.SubagentBlocked), payload.InterruptKind);
        Assert.Equal("x", payload.Summary);
    }

    private static async Task<Guid> CreateAsync(
        IDysonSessionRepository sessions,
        int runtimeId,
        Guid? parentId,
        string mode,
        string title)
    {
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = runtimeId,
            ParentSessionId = parentId,
            AgentMode = mode,
            Title = title,
            SystemPromptSnapshot = title,
            Status = DysonSessionStatus.Active,
        });
        Assert.False(created.IsError, created.IsError ? created.Error : null);
        return created.Value;
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(string mode = DysonAgentModes.Work)
            : base(mode, new DysonAgentSessionConfig(), new StubProvider())
        {
        }

        public StubSession(string mode, IDysonSessionRepository store, Guid persistenceId)
            : base(mode, new DysonAgentSessionConfig(), new StubProvider())
        {
            SessionStore = store;
            SetPersistenceId(persistenceId);
        }

        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

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

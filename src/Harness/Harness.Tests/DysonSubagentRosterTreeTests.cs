using System.Text.Json;

using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ListMetaAgentDrones / ListSubagents nest each child's whole descendant tree: live state where loaded,
/// persisted rows otherwise, deleted rows left out, descendant lastReport cut to a one-line preview.
/// </summary>
public class DysonSubagentRosterTreeTests
{
    [Fact]
    public async Task Roster_nests_live_and_persisted_descendants_with_status_and_skips_deleted()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _keepAlive = conn;
        var sessions = DysonTempDb.Sessions(accessor);

        var rootId = await CreateAsync(sessions, 0, null, DysonAgentModes.MetaAgent, "root", DysonSessionStatus.Active);
        var droneId = await CreateAsync(sessions, 1, rootId, DysonAgentModes.MetaAgentDrone, "drone", DysonSessionStatus.Active);
        var workerId = await CreateAsync(sessions, 2, droneId, DysonAgentModes.Drone, "worker", DysonSessionStatus.Completed);
        var subWorkerId = await CreateAsync(sessions, 1, workerId, DysonAgentModes.Explore, "sub-worker", DysonSessionStatus.Interrupted);
        var deletedId = await CreateAsync(sessions, 3, droneId, DysonAgentModes.Drone, "deleted worker", DysonSessionStatus.Completed);

        var longReport = "line one\n" + new string('x', 400);
        Assert.False((await sessions.AppendLogAsync(DysonSessionLogPayload.CreateEntry(
            workerId,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(DysonSessionStatus.Completed, longReport)))).IsError);
        Assert.False((await sessions.DeleteSessionAsync(deletedId)).IsError);

        // Root and drone are loaded; worker and sub-worker exist only in the store (e.g. after restart).
        var root = new StubSession(DysonAgentModes.MetaAgent, sessions, rootId);
        var drone = new StubSession(DysonAgentModes.MetaAgentDrone, sessions, droneId);
        root.RegisterForTest(drone);
        // A loaded child whose row was deleted is not shown.
        var ghost = new StubSession(DysonAgentModes.Drone, sessions, Guid.NewGuid());
        drone.RegisterForTest(ghost);
        Assert.True(drone.TryMarkTerminal(DysonSessionStatus.Completed, longReport));

        using (var doc = JsonDocument.Parse(await root.FormatChildRosterJsonAsync(includeReports: true)))
        {
            var top = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal(droneId, top.GetProperty("persistenceId").GetGuid());
            Assert.Equal("Completed", top.GetProperty("status").GetString());
            Assert.Equal(longReport, top.GetProperty("lastReport").GetString());

            var worker = Assert.Single(top.GetProperty("children").EnumerateArray());
            Assert.Equal(workerId, worker.GetProperty("persistenceId").GetGuid());
            Assert.Equal(2, worker.GetProperty("subagentId").GetInt32());
            Assert.Equal("Completed", worker.GetProperty("status").GetString());
            var preview = worker.GetProperty("lastReport").GetString()!;
            Assert.StartsWith("line one x", preview, StringComparison.Ordinal);
            Assert.EndsWith("…", preview, StringComparison.Ordinal);
            Assert.Equal(DysonAgentSession.RosterDescendantReportChars + 1, preview.Length);

            var subWorker = Assert.Single(worker.GetProperty("children").EnumerateArray());
            Assert.Equal(subWorkerId, subWorker.GetProperty("persistenceId").GetGuid());
            Assert.Equal("Interrupted", subWorker.GetProperty("status").GetString());
            Assert.Equal("explore", subWorker.GetProperty("kind").GetString());
            Assert.Empty(subWorker.GetProperty("children").EnumerateArray());
        }

        using (var doc = JsonDocument.Parse(await root.FormatListSubagentsJsonAsync()))
        {
            var worker = Assert.Single(doc.RootElement[0].GetProperty("children").EnumerateArray());
            Assert.False(worker.TryGetProperty("lastReport", out _));
            var subWorker = Assert.Single(worker.GetProperty("children").EnumerateArray());
            Assert.Equal("Interrupted", subWorker.GetProperty("status").GetString());
        }
    }

    [Fact]
    public void Report_preview_is_one_line_and_short_text_is_kept()
    {
        Assert.Equal("a b", DysonAgentSession.RosterReportPreview("a\n\n  b"));
        Assert.Null(DysonAgentSession.RosterReportPreview(null));
    }

    private static async Task<Guid> CreateAsync(
        IDysonSessionRepository sessions,
        int runtimeId,
        Guid? parentId,
        string mode,
        string title,
        DysonSessionStatus status)
    {
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = runtimeId,
            ParentSessionId = parentId,
            AgentMode = mode,
            Title = title,
            SystemPromptSnapshot = title,
            Status = status,
        });
        Assert.False(created.IsError, created.IsError ? created.Error : null);
        return created.Value;
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(string mode, IDysonSessionRepository store, Guid persistenceId)
            : base(mode, new DysonAgentSessionConfig(), new StubProvider())
        {
            SessionStore = store;
            SetPersistenceId(persistenceId);
        }

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

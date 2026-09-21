using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: stale-agent listing is null at 20, non-null at 21, oldest-first, running ignored.
/// </summary>
public class DysonMetaMaintenanceInstructionTests
{
    [Fact]
    public void Listing_null_at_20_non_null_at_21_oldest_first()
    {
        var twenty = Enumerable.Range(1, 20)
            .Select(i => new DysonMetaMaintenanceFinishedAgent(i, $"a{i}", DateTime.UtcNow.AddMinutes(-i)))
            .ToArray();
        Assert.Null(DysonMetaMaintenanceTick.BuildStaleAgentListing(twenty));

        var twentyOne = twenty
            .Concat([new DysonMetaMaintenanceFinishedAgent(21, "oldest", DateTime.UtcNow.AddDays(-2))])
            .ToArray();
        var listing = DysonMetaMaintenanceTick.BuildStaleAgentListing(twentyOne);
        Assert.NotNull(listing);
        Assert.Contains("agentId=21", listing, StringComparison.Ordinal);
        Assert.Contains("title=\"oldest\"", listing, StringComparison.Ordinal);
        Assert.Contains("keep limit", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void Instruction_always_names_roster_and_plan_tools()
    {
        var text = DysonMetaMaintenanceTick.BuildMaintenanceInstruction([]);
        Assert.Equal(DysonMetaMaintenanceTick.Instruction, text);
        Assert.Contains("ListMetaAgentDrones", text, StringComparison.Ordinal);
        Assert.Contains("ListPlans", text, StringComparison.Ordinal);
        Assert.Contains("DeleteMetaAgent", text, StringComparison.Ordinal);
        Assert.Contains("DeletePlan", text, StringComparison.Ordinal);
        Assert.Contains("RemoveTodos", text, StringComparison.Ordinal);

        var over = Enumerable.Range(1, 21)
            .Select(i => new DysonMetaMaintenanceFinishedAgent(i, null, null))
            .ToArray();
        var withList = DysonMetaMaintenanceTick.BuildMaintenanceInstruction(over);
        Assert.StartsWith(DysonMetaMaintenanceTick.Instruction, withList, StringComparison.Ordinal);
        Assert.Contains("agentId=1", withList, StringComparison.Ordinal);
        Assert.Contains("agentId=21", withList, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectFinishedAgents_ignores_running_and_orders_oldest_first()
    {
        var parent = new StubSession(DysonAgentModes.MetaAgent);
        var running = new StubSession(DysonAgentModes.MetaAgentDrone);
        var older = new StubSession(DysonAgentModes.Explore);
        var newer = new StubSession(DysonAgentModes.MetaAgentDrone);
        parent.RegisterForTest(running);
        parent.RegisterForTest(older);
        parent.RegisterForTest(newer);

        Assert.True(older.TryMarkTerminal(DysonSessionStatus.Completed, "old"));
        older.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "done",
            StartedUtc = DateTime.UtcNow.AddHours(-2),
            CompletedUtc = DateTime.UtcNow.AddHours(-2),
        });
        Assert.True(newer.TryMarkTerminal(DysonSessionStatus.Failed, "new"));
        newer.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "fail",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
        });

        var finished = DysonMetaMaintenanceTick.CollectFinishedAgents(parent);
        Assert.Equal(2, finished.Count);
        Assert.Equal(older.Id, finished[0].AgentId);
        Assert.Equal(newer.Id, finished[1].AgentId);
        Assert.DoesNotContain(finished, a => a.AgentId == running.Id);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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

using DysonHarness;
using Harness.UI;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>
/// Scripted Meta Agent demo: preview gate, Aester ledger scenario order, and cancel.
/// </summary>
public class DysonDemoScenarioTests
{
    [Fact]
    public void Demo_hooks_are_absent_from_stable_builds()
    {
#if DYSON_PREVIEW
        const bool compiled = true;
#else
        const bool compiled = false;
#endif
        Assert.Equal(compiled, DysonPreviewFeatures.DemoModeEnabled);
    }

#if DYSON_PREVIEW
    [Fact]
    public async Task Scenario_runs_to_completion_against_a_fake_hook()
    {
        var hooks = new FakeHooks();
        var scenario = new AesterShardingDemoScenario();

        var result = await scenario.RunAsync(hooks, CancellationToken.None);
        var endedAt = hooks.Clock.Elapsed;

        Assert.True(result.IsSuccess, result.Error);
        Assert.DoesNotContain("cleanup", hooks.Calls);

        // Final segment: visualization request after the sharding answer, one drawing drone, the diagram, then a 2 s hold and the end.
        var answeredAt = hooks.Calls.FindIndex(c => c.StartsWith("answer:", StringComparison.Ordinal));
        var vizAskAt = hooks.Calls.FindIndex(c => c.StartsWith("user:Can you visualize the data schema", StringComparison.Ordinal));
        var drawerAt = hooks.Calls.FindIndex(c => c.StartsWith("agent:Meta Agent Drone:Visualize Aester ledger schema:parent=root", StringComparison.Ordinal));
        var postedAt = hooks.Calls.FindIndex(c => c.StartsWith("viz:Aester ledger schema", StringComparison.Ordinal));
        Assert.True(answeredAt >= 0 && vizAskAt > answeredAt && drawerAt > vizAskAt && postedAt > drawerAt,
            "visualization request, drone, and diagram follow the sharding answer in order");
        var drawerId = hooks.Agents.Single(a => a.Title == "Visualize Aester ledger schema").PersistenceId;
        Assert.True(hooks.Calls.Count(c => c.StartsWith($"status:{drawerId}:{DysonSessionStatus.Active}:", StringComparison.Ordinal)) >= 2,
            "the drawing drone posts progress before it reports");
        Assert.Contains(hooks.Calls, c => c.StartsWith($"status:{drawerId}:{DysonSessionStatus.Completed}:", StringComparison.Ordinal));
        Assert.Equal("viz-open", hooks.Calls[^1]);
        Assert.Contains("<svg", hooks.Visualization!.Html, StringComparison.Ordinal);
        foreach (var table in new[] { "account", "journal_entry", "journal_line", "balance_snapshot", "Shard key" })
            Assert.Contains(table, hooks.Visualization.Html, StringComparison.Ordinal);
        Assert.Contains("#e09b58", hooks.Visualization.Css, StringComparison.Ordinal);
        var held = endedAt - hooks.OpenedVisualizationAt;
        Assert.True(held >= TimeSpan.FromMilliseconds(AesterShardingDemoScenario.VisualizationHoldMs - 100)
            && held < TimeSpan.FromMilliseconds(AesterShardingDemoScenario.VisualizationHoldMs + 1500),
            $"the run ends about {AesterShardingDemoScenario.VisualizationHoldMs} ms after the visualization opens (held {held.TotalMilliseconds} ms)");

        var confirmationAt = hooks.Calls.FindIndex(c =>
            c.StartsWith("assistant:", StringComparison.Ordinal) && c.Contains("planning drone", StringComparison.Ordinal));
        var plannerAt = hooks.Calls.FindIndex(c =>
            c.Contains("Meta Agent Drone:Draft the schema plan:", StringComparison.Ordinal));
        Assert.True(confirmationAt >= 0 && plannerAt > confirmationAt,
            "the confirmation reply lands before the planning drone");

        var explores = hooks.Agents.Where(a => a.Mode == "Explore").ToList();
        Assert.Equal(3, explores.Count);
        Assert.Equal(
            [
                "Explore the ledger domain model",
                "Explore the persistence setup",
                "Check the existing migration history",
            ],
            explores.Select(a => a.Title).ToArray());

        var drones = hooks.Agents.Where(a => a.Mode == "Meta Agent Drone").ToList();
        Assert.Equal(3, drones.Count);
        Assert.Equal("Draft the schema plan", drones[0].Title);
        Assert.Null(drones[0].ParentPersistenceId);
        Assert.Equal("Build the Aester ledger schema", drones[1].Title);
        Assert.Null(drones[1].ParentPersistenceId);

        var orchestratorId = drones[1].PersistenceId;
        var workers = hooks.Agents.Where(a => a.Mode == "Drone").ToList();
        Assert.Equal(6, workers.Count);
        Assert.All(workers, w => Assert.Equal(orchestratorId, w.ParentPersistenceId));

        Assert.Contains("plan:Ledger schema for Aester Financials:Draft:active", hooks.Calls);
        Assert.Contains("plan:Auth service token rotation:Completed:active", hooks.Calls);
        Assert.Contains("plan:Payments webhook retry queue:Completed:active", hooks.Calls);
        Assert.Contains("plan:Statement PDF export:Stale:active", hooks.Calls);
        Assert.Contains("plan:Customer notification preferences:Completed:archived", hooks.Calls);
        Assert.Contains("plan:Legacy CSV statement import:Stale:archived", hooks.Calls);
        var firstUserAt = hooks.Calls.FindIndex(c => c.StartsWith("user:", StringComparison.Ordinal));
        var historyAt = hooks.Calls.FindIndex(c => c.StartsWith("plan:Auth service token rotation:", StringComparison.Ordinal));
        Assert.True(historyAt >= 0 && firstUserAt > historyAt, "history plans are seeded before the first user message");
        Assert.Contains("open:1", hooks.Calls);
        Assert.Contains($"plan-status:1:{DysonPlanStatus.Building}", hooks.Calls);
        Assert.Contains($"plan-status:1:{DysonPlanStatus.Completed}", hooks.Calls);

        var planAt = hooks.Calls.FindIndex(c => c.StartsWith("plan:", StringComparison.Ordinal));
        var orchestratorAt = hooks.Calls.FindIndex(c =>
            c.Contains("Meta Agent Drone:Build the Aester ledger schema:", StringComparison.Ordinal));
        Assert.True(planAt >= 0 && orchestratorAt > planAt, "plan must be created before the orchestrator drone");

        Assert.Contains("question:Which shard key should the ledger use?", hooks.Calls);
        Assert.Contains("answer:Don't shard yet — revisit after the PostgreSQL cutover", hooks.Calls);
        var questionAt = hooks.Calls.FindIndex(c => c.StartsWith("question:", StringComparison.Ordinal));
        var answerAt = hooks.Calls.FindIndex(c => c.StartsWith("answer:", StringComparison.Ordinal));
        Assert.True(questionAt >= 0 && answerAt > questionAt, "answer must follow the question");

        Assert.Contains(hooks.Calls, c =>
            c.StartsWith("assistant:", StringComparison.Ordinal) && c.Contains("All six slices are merged", StringComparison.Ordinal));
        Assert.Contains(hooks.Calls, c =>
            c.StartsWith("assistant:", StringComparison.Ordinal) && c.Contains("no sharding for now", StringComparison.Ordinal));

        var firstWorkerCompleted = hooks.Calls.FindIndex(c =>
            c.StartsWith("status:", StringComparison.Ordinal)
            && c.Contains($":{DysonSessionStatus.Completed}:", StringComparison.Ordinal)
            && workers.Any(w => c.StartsWith($"status:{w.PersistenceId}:", StringComparison.Ordinal)));
        var lastWorkerAdded = hooks.Calls.FindLastIndex(c => c.StartsWith("agent:Drone:", StringComparison.Ordinal));
        Assert.True(lastWorkerAdded >= 0 && firstWorkerCompleted > lastWorkerAdded,
            "all six drones are added before the first worker status Completed");
    }

    [Fact]
    public async Task Scenario_honors_cancel()
    {
        using var cts = new CancellationTokenSource();
        var hooks = new FakeHooks(cts);
        var scenario = new AesterShardingDemoScenario();

        try
        {
            await scenario.RunAsync(hooks, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The scenario may propagate the token instead of returning a failed result.
        }

        Assert.InRange(hooks.Calls.Count, 1, 8);
        Assert.DoesNotContain("cleanup", hooks.Calls);
        Assert.Contains("plan:Auth service token rotation:Completed:active", hooks.Calls);
        Assert.DoesNotContain(hooks.Calls, c => c.Contains("Ledger schema", StringComparison.Ordinal));
        Assert.StartsWith("start:", hooks.Calls[0], StringComparison.Ordinal);
    }

    private sealed record RecordedAgent(string Mode, string Title, Guid? ParentPersistenceId, Guid PersistenceId);

    private sealed class FakeHooks : IDysonDemoHooks
    {
        private readonly CancellationTokenSource? _cancelOnEnter;

        public FakeHooks(CancellationTokenSource? cancelOnEnter = null) => _cancelOnEnter = cancelOnEnter;

        public List<string> Calls { get; } = [];

        public List<RecordedAgent> Agents { get; } = [];

        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();

        public DysonDemoVisualization? Visualization { get; private set; }

        public TimeSpan OpenedVisualizationAt { get; private set; }

        public Task<Result<DysonDemoStart, string>> StartAsync(string workDirectoryName, CancellationToken ct)
        {
            Calls.Add($"start:{workDirectoryName}");
            Enter();
            return Task.FromResult(Result<DysonDemoStart, string>.AsValue(
                new DysonDemoStart(Guid.NewGuid(), Guid.NewGuid(), null)));
        }

        public Task<VoidResult<string>> AppendUserTurnAsync(string instruction, string assistantText, CancellationToken ct)
        {
            Enter();
            Calls.Add($"user:{instruction}");
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<Result<Guid?, string>> AppendAssistantMessageAsync(
            string message,
            DysonDemoQuestion? question,
            CancellationToken ct)
        {
            Enter();
            Calls.Add($"assistant:{message}");
            if (question is null)
                return Task.FromResult(Result<Guid?, string>.AsValue(null));

            Calls.Add($"question:{question.Question}");
            foreach (var choice in question.Choices)
                Calls.Add($"choice:{choice}");

            return Task.FromResult(Result<Guid?, string>.AsValue((Guid?)Guid.NewGuid()));
        }

        public Task<VoidResult<string>> AnswerQuestionAsync(Guid questionId, string selectedChoice, CancellationToken ct)
        {
            Enter();
            Calls.Add($"answer:{selectedChoice}");
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<Result<Guid, string>> PostVisualizationAsync(
            string message,
            DysonDemoVisualization visualization,
            CancellationToken ct)
        {
            Enter();
            Visualization = visualization;
            Calls.Add($"viz:{visualization.Title}");
            return Task.FromResult(Result<Guid, string>.AsValue(Guid.NewGuid()));
        }

        public Task<VoidResult<string>> OpenVisualizationAsync(CancellationToken ct)
        {
            Enter();
            OpenedVisualizationAt = Clock.Elapsed;
            Calls.Add("viz-open");
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<Result<long, string>> CreatePlanAsync(
            string title,
            string markdown,
            CancellationToken ct,
            DysonPlanStatus status = DysonPlanStatus.Draft,
            DateTime? updatedUtc = null,
            bool archived = false)
        {
            Enter();
            Calls.Add($"plan:{title}:{status}:{(archived ? "archived" : "active")}");
            return Task.FromResult(Result<long, string>.AsValue(1L));
        }

        public Task<VoidResult<string>> OpenPlanAsync(long planId, CancellationToken ct)
        {
            Enter();
            Calls.Add($"open:{planId}");
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<VoidResult<string>> SetPlanStatusAsync(long planId, DysonPlanStatus status, CancellationToken ct)
        {
            Enter();
            Calls.Add($"plan-status:{planId}:{status}");
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<Result<DysonDemoAgent, string>> AddAgentAsync(
            Guid? parentPersistenceId,
            string title,
            string mode,
            CancellationToken ct)
        {
            Enter();
            var agent = new DysonDemoAgent(Guid.NewGuid(), 1);
            Agents.Add(new RecordedAgent(mode, title, parentPersistenceId, agent.PersistenceId));
            Calls.Add($"agent:{mode}:{title}:parent={(parentPersistenceId?.ToString() ?? "root")}");
            return Task.FromResult(Result<DysonDemoAgent, string>.AsValue(agent));
        }

        public Task<VoidResult<string>> SetAgentStatusAsync(
            Guid persistenceId,
            DysonSessionStatus status,
            string? activity,
            CancellationToken ct)
        {
            Enter();
            Calls.Add($"status:{persistenceId}:{status}:{activity}");
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<VoidResult<string>> CleanupAsync(CancellationToken ct)
        {
            Enter();
            Calls.Add("cleanup");
            return Task.FromResult(VoidResult<string>.Success);
        }

        private void Enter() => _cancelOnEnter?.Cancel();
    }
#endif
}

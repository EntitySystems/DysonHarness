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

        Assert.True(result.IsSuccess, result.Error);
        Assert.DoesNotContain("cleanup", hooks.Calls);

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
        Assert.Equal(2, drones.Count);
        Assert.Equal("Draft the schema plan", drones[0].Title);
        Assert.Null(drones[0].ParentPersistenceId);
        Assert.Equal("Build the Aester ledger schema", drones[1].Title);
        Assert.Null(drones[1].ParentPersistenceId);

        var orchestratorId = drones[1].PersistenceId;
        var workers = hooks.Agents.Where(a => a.Mode == "Drone").ToList();
        Assert.Equal(6, workers.Count);
        Assert.All(workers, w => Assert.Equal(orchestratorId, w.ParentPersistenceId));

        Assert.Contains("plan:Ledger schema for Aester Financials", hooks.Calls);
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

        Assert.InRange(hooks.Calls.Count, 1, 2);
        Assert.DoesNotContain("cleanup", hooks.Calls);
        Assert.StartsWith("start:", hooks.Calls[0], StringComparison.Ordinal);
    }

    private sealed record RecordedAgent(string Mode, string Title, Guid? ParentPersistenceId, Guid PersistenceId);

    private sealed class FakeHooks : IDysonDemoHooks
    {
        private readonly CancellationTokenSource? _cancelOnEnter;

        public FakeHooks(CancellationTokenSource? cancelOnEnter = null) => _cancelOnEnter = cancelOnEnter;

        public List<string> Calls { get; } = [];

        public List<RecordedAgent> Agents { get; } = [];

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

        public Task<Result<long, string>> CreatePlanAsync(string title, string markdown, CancellationToken ct)
        {
            Enter();
            Calls.Add($"plan:{title}");
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

using System.Linq;

namespace DysonHarness;

/// <summary>
/// ponytail: assert-only checks for inter-agent events + AskQuestion (no test framework).
/// Covers: any-wait deadlock on TriggerParentEvent only; Respond while waiting; interrupt cancels
/// event wait; non-interrupt fails while child waiting; layer omit; Q/A formatter.
/// </summary>
public static class DysonParentEventSelfCheck
{
    public static void Run()
    {
        AssertLayerGating();
        AssertReparentRestoresChildTools();
        AssertFormatter();
        AssertDeadlockAndRespondWhileWaiting().GetAwaiter().GetResult();
        AssertInterruptCancelsEventWait().GetAwaiter().GetResult();
        AssertNonInterruptFailsWhileWaiting().GetAwaiter().GetResult();
    }

    private static void AssertLayerGating()
    {
        var root = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        root.ConfigureInterAgentTools(0);
        AssertHas(root, "AskQuestion");
        AssertMissing(root, "AskQuestionFromParent");
        AssertMissing(root, "TriggerParentEvent");
        AssertHas(root, "RespondToSubagentEvent");
        AssertHas(root, "TriggerSubagentEvent");

        var l1 = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        l1.ConfigureInterAgentTools(1);
        AssertMissing(l1, "AskQuestion");
        AssertHas(l1, "AskQuestionFromParent");
        AssertHas(l1, "TriggerParentEvent");
        AssertHas(l1, "RespondToSubagentEvent");
        AssertHas(l1, "TriggerSubagentEvent");

        var deep = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        deep.ConfigureInterAgentTools(2);
        AssertMissing(deep, "AskQuestion");
        AssertMissing(deep, "AskQuestionFromParent");
        AssertHas(deep, "TriggerParentEvent");
        AssertHas(deep, "RespondToSubagentEvent");
        AssertHas(deep, "TriggerSubagentEvent");
    }

    /// <summary>
    /// Simulates cold-load: child gated as depth 0, then RestoreRegisteredSubagent restores L1 tools.
    /// </summary>
    private static void AssertReparentRestoresChildTools()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();

        var child = new StubSession();
        child.ConfigureRootForTest(); // depth-0 gate (Parent still null) — strips TriggerParentEvent
        AssertMissing(child.McpPipeline, "TriggerParentEvent");
        AssertMissing(child.McpPipeline, "AskQuestionFromParent");
        AssertHas(child.McpPipeline, "AskQuestion");

        child.SetRuntimeIdForTest(1);
        parent.RestoreRegisteredSubagent(child);

        if (!ReferenceEquals(child.Parent, parent) || child.ComputeDepth() != 1)
            throw new InvalidOperationException("Expected child Parent linked at depth 1 after restore.");

        AssertHas(child.McpPipeline, "TriggerParentEvent");
        AssertHas(child.McpPipeline, "AskQuestionFromParent");
        AssertMissing(child.McpPipeline, "AskQuestion");
    }

    private static void AssertFormatter()
    {
        var questions = new[]
        {
            new DysonAskQuestionItem
            {
                Prompt = "How many eggs does the recipe need?",
                Options = ["4", "6", "12"],
                AllowMultiple = false,
            },
            new DysonAskQuestionItem
            {
                Prompt = "Sides?",
                Options = ["toast", "bacon"],
                AllowMultiple = true,
            },
        };

        var answers = new[]
        {
            new DysonAskQuestionAnswer
            {
                Selected = ["6"],
                Custom = "with the option of 2 more for a bigger serving",
            },
            new DysonAskQuestionAnswer { Skipped = true },
        };

        var formatted = DysonAskQuestion.FormatAnswers(questions, answers);
        if (!formatted.Contains("Q1 - How many eggs does the recipe need?", StringComparison.Ordinal)
            || !formatted.Contains("A1 - 6, with the option of 2 more for a bigger serving", StringComparison.Ordinal)
            || !formatted.Contains("Q2 - Sides?", StringComparison.Ordinal)
            || !formatted.Contains("A2 - [skipped]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AskQuestion formatter shape wrong:\n" + formatted);
        }

        var parsed = DysonAskQuestion.ParseQuestionsJson(
            """{"questions":[{"prompt":"P","options":["a"]},{"prompt":"Q","options":["b"],"allowMultiple":true}]}""");
        if (parsed.IsError || parsed.Value.Count != 2 || !parsed.Value[1].AllowMultiple)
            throw new InvalidOperationException("ParseQuestionsJson failed: " + (parsed.IsError ? parsed.Error : "shape"));

        var tooMany = DysonAskQuestion.ParseQuestionsJson(
            "{\"questions\":[" + string.Join(",", Enumerable.Repeat("{\"prompt\":\"x\",\"options\":[\"a\"]}", 9)) + "]}");
        if (!tooMany.IsError || tooMany.Error.IndexOf("8", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Expected max-8 validation.");
    }

    private static async Task AssertDeadlockAndRespondWhileWaiting()
    {
        var parent = new StubSession();
        var childA = new StubSession();
        var childB = new StubSession();
        parent.RegisterForTest(childA);
        parent.RegisterForTest(childB);

        // Start Wait on childA (never terminals) — Trigger from childB must deadlock.
        using var waitCts = new CancellationTokenSource();
        var waitTask = parent.WaitForSubagentAsync(childA.Id, timeoutMs: 60_000, waitCts.Token);

        // Give wait a moment to register waiting ids.
        await Task.Delay(25).ConfigureAwait(false);
        if (!parent.IsWaitingOnAnySubagent || !parent.WaitingOnSubagentIds.Contains(childA.Id))
            throw new InvalidOperationException("Expected parent IsWaitingOnAnySubagent during Wait.");

        var deadlock = await childB.TriggerParentEventAsync("status", "ping", CancellationToken.None)
            .ConfigureAwait(false);
        if (!deadlock.IsError
            || deadlock.Error.IndexOf("waiting on subagent", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Expected TriggerParentEvent deadlock while WaitForSubagent: " +
                (deadlock.IsError ? deadlock.Error : deadlock.Value));
        }

        // Pre-register a pending event while NOT waiting, then Wait, then Respond must succeed.
        waitCts.Cancel();
        try { await waitTask.ConfigureAwait(false); } catch { /* cancelled */ }

        // Fresh wait-free trigger then Respond mid-wait on another child.
        var triggerTask = childB.TriggerParentEventAsync("status", "need-reply", CancellationToken.None);
        await Task.Delay(25).ConfigureAwait(false);

        var events = parent.PendingOrRecentParentEvents
            .Where(e => e.Status == DysonParentEventStatus.Pending && e.SubagentId == childB.Id)
            .ToArray();
        if (events.Length != 1)
            throw new InvalidOperationException("Expected one pending parent event from childB.");

        using var wait2 = new CancellationTokenSource();
        var wait2Task = parent.WaitForSubagentAsync(childA.Id, timeoutMs: 60_000, wait2.Token);
        await Task.Delay(25).ConfigureAwait(false);

        var responded = parent.RespondToSubagentEvent(childB.Id, events[0].EventId, "ack");
        if (responded.IsError)
            throw new InvalidOperationException("Respond while waiting should succeed: " + responded.Error);

        var triggerResult = await triggerTask.ConfigureAwait(false);
        if (triggerResult.IsError || triggerResult.Value != "ack")
        {
            throw new InvalidOperationException(
                "Trigger should unblock with reply: " +
                (triggerResult.IsError ? triggerResult.Error : triggerResult.Value));
        }

        wait2.Cancel();
        try { await wait2Task.ConfigureAwait(false); } catch { /* cancelled */ }
    }

    private static async Task AssertInterruptCancelsEventWait()
    {
        var parent = new StubSession();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var triggerTask = child.TriggerParentEventAsync("status", "hold", CancellationToken.None);
        await Task.Delay(25).ConfigureAwait(false);
        if (!child.HasPendingParentEventWait)
            throw new InvalidOperationException("Expected child HasPendingParentEventWait.");

        var interrupted = await parent
            .TriggerSubagentEventAsync(child.Id, "new instructions", interruptSubagent: true)
            .ConfigureAwait(false);
        if (interrupted.IsError
            || interrupted.Value.IndexOf("interrupted", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Expected interrupt status: " +
                (interrupted.IsError ? interrupted.Error : interrupted.Value));
        }

        var triggerResult = await triggerTask.ConfigureAwait(false);
        if (!triggerResult.IsError
            || triggerResult.Error.IndexOf("cancelled by parent TriggerSubagentEvent", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "Expected cancelled event wait: " +
                (triggerResult.IsError ? triggerResult.Error : triggerResult.Value));
        }
    }

    private static async Task AssertNonInterruptFailsWhileWaiting()
    {
        var parent = new StubSession();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var triggerTask = child.TriggerParentEventAsync("status", "hold", CancellationToken.None);
        await Task.Delay(25).ConfigureAwait(false);

        var queued = await parent
            .TriggerSubagentEventAsync(child.Id, "should fail", interruptSubagent: false)
            .ConfigureAwait(false);
        if (!queued.IsError
            || queued.Error.IndexOf("awaiting a parent-event reply", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Expected non-interrupt fail while child waiting: " +
                (queued.IsError ? queued.Error : queued.Value));
        }

        var pending = parent.PendingOrRecentParentEvents
            .First(e => e.Status == DysonParentEventStatus.Pending);
        parent.RespondToSubagentEvent(child.Id, pending.EventId, "done");
        _ = await triggerTask.ConfigureAwait(false);
    }

    private static void AssertHas(DysonMcpPipeline pipeline, string name)
    {
        if (!pipeline.Tools.ContainsKey(name))
            throw new InvalidOperationException($"Expected tool {name} in catalog.");
    }

    private static void AssertMissing(DysonMcpPipeline pipeline, string name)
    {
        if (pipeline.Tools.ContainsKey(name))
            throw new InvalidOperationException($"Did not expect tool {name} in catalog.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

        public void SetRuntimeIdForTest(int runtimeId) => Id = runtimeId;

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
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

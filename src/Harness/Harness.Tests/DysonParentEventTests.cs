using System.Linq;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only checks for inter-agent events + AskQuestion (Xunit Fact).
/// Covers: any-wait deadlock on TriggerParentEvent only; Respond while waiting; interrupt cancels
/// event wait; non-interrupt fails while child waiting; inject after completed report reopens and
/// Wait waits for the second handoff; layer omit; Q/A formatter.
/// </summary>
public class DysonParentEventTests
{
    [Fact]
    public async Task Run()
    {
        AssertLayerGating();
        AssertReparentRestoresChildTools();
        AssertFormatter();
        await AssertDeadlockAndRespondWhileWaiting();
        await AssertInterruptCancelsEventWait();
        await AssertNonInterruptFailsWhileWaiting();
        await AssertInjectAfterCompletedReportWaitsForSecond();
        await AssertWaitConsumeMarker();
    }

    private static void AssertLayerGating()
    {
        var root = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, ["Pwsh"]);
        root.ConfigureInterAgentTools(0);
        AssertHas(root, "AskQuestion");
        AssertMissing(root, "AskQuestionFromParent");
        AssertHas(root, "PromptUserDialog");
        AssertMissing(root, "PromptUserDialogFromParent");
        AssertMissing(root, "TriggerParentEvent");
        AssertHas(root, "RespondToSubagentEvent");
        AssertHas(root, "TriggerSubagentEvent");
        AssertHas(root, "SubscribeToLongRunningShellCompletion");
        AssertHas(root, "WaitForLongRunningShellCompletion");

        var l1 = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, ["Pwsh"]);
        l1.ConfigureInterAgentTools(1);
        AssertMissing(l1, "AskQuestion");
        AssertHas(l1, "AskQuestionFromParent");
        AssertMissing(l1, "PromptUserDialog");
        AssertHas(l1, "PromptUserDialogFromParent");
        AssertHas(l1, "TriggerParentEvent");
        AssertHas(l1, "RespondToSubagentEvent");
        AssertHas(l1, "TriggerSubagentEvent");
        AssertMissing(l1, "SubscribeToLongRunningShellCompletion");
        AssertHas(l1, "WaitForLongRunningShellCompletion");

        var deep = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, ["Pwsh"]);
        deep.ConfigureInterAgentTools(2);
        AssertMissing(deep, "AskQuestion");
        AssertMissing(deep, "AskQuestionFromParent");
        AssertMissing(deep, "PromptUserDialog");
        AssertMissing(deep, "PromptUserDialogFromParent");
        AssertHas(deep, "TriggerParentEvent");
        AssertHas(deep, "RespondToSubagentEvent");
        AssertHas(deep, "TriggerSubagentEvent");
        AssertMissing(deep, "SubscribeToLongRunningShellCompletion");
        AssertHas(deep, "WaitForLongRunningShellCompletion");
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
        AssertMissing(child.McpPipeline, "PromptUserDialogFromParent");
        AssertHas(child.McpPipeline, "AskQuestion");
        AssertHas(child.McpPipeline, "PromptUserDialog");
        AssertHas(child.McpPipeline, "SubscribeToLongRunningShellCompletion");
        AssertHas(child.McpPipeline, "WaitForLongRunningShellCompletion");

        child.SetRuntimeIdForTest(1);
        parent.RestoreRegisteredSubagent(child);

        if (!ReferenceEquals(child.Parent, parent) || child.ComputeDepth() != 1)
            throw new InvalidOperationException("Expected child Parent linked at depth 1 after restore.");

        AssertHas(child.McpPipeline, "TriggerParentEvent");
        AssertHas(child.McpPipeline, "AskQuestionFromParent");
        AssertHas(child.McpPipeline, "PromptUserDialogFromParent");
        AssertMissing(child.McpPipeline, "AskQuestion");
        AssertMissing(child.McpPipeline, "PromptUserDialog");
        AssertMissing(child.McpPipeline, "SubscribeToLongRunningShellCompletion");
        AssertHas(child.McpPipeline, "WaitForLongRunningShellCompletion");
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

        if (parent.IsWaitingOnAnySubagent || parent.WaitingOnSubagentIds.Contains(childA.Id))
            throw new InvalidOperationException("Expected waiting id cleared after Wait cancel.");
        if (parent.HasWaitConsumedCompletion(childA.Id)
            || parent.ShouldSuppressWaitedCompletionAutoTurn(childA.Id))
        {
            throw new InvalidOperationException(
                "Cancel must not mark wait-consumed or suppress completion auto-turns.");
        }
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

    /// <summary>
    /// After a completed report, parent inject reopens the child and WaitForSubagent waits for the
    /// second handoff (not the old Completed). Hanging PromptHarnessTurnAsync so kickoff cannot
    /// mark Failed before the second submit.
    /// </summary>
    private static async Task AssertInjectAfterCompletedReportWaitsForSecond()
    {
        var parent = new StubSession();
        var child = new HangingChildSession();
        parent.RegisterForTest(child);

        try
        {
            var first = await child.SubmitSubagentReportAsync("first handoff").ConfigureAwait(false);
            if (first.IsError)
                throw new InvalidOperationException("Expected first completed report ok: " + first.Error);
            if (child.Status != DysonSessionStatus.Completed)
                throw new InvalidOperationException("Expected first report to mark Completed.");

            var injected = await parent
                .TriggerSubagentEventAsync(child.Id, "new assignment")
                .ConfigureAwait(false);
            if (injected.IsError)
                throw new InvalidOperationException("Expected inject after report ok: " + injected.Error);

            var json = injected.Value;
            if ((json.IndexOf("\"reopened\":true", StringComparison.Ordinal) < 0
                    && json.IndexOf("\"reopened\": true", StringComparison.Ordinal) < 0)
                || json.IndexOf("queued", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    "Expected inject JSON with reopened:true and queued: " + json);
            }

            if (child.Status != DysonSessionStatus.Active)
                throw new InvalidOperationException("Expected reopen to Active, got " + child.Status);
            if (!string.Equals(child.LastReportSummary, "first handoff", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected LastReportSummary unchanged until second submit.");

            await child.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var waitTask = parent.WaitForSubagentAsync(child.Id, timeoutMs: 5000);
            await Task.Delay(80).ConfigureAwait(false);
            if (waitTask.IsCompleted)
            {
                var early = await waitTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    "WaitForSubagent returned the old Completed instead of waiting for the second report: " +
                    (early.IsError ? early.Error : early.Value));
            }

            var second = await child.SubmitSubagentReportAsync("second handoff").ConfigureAwait(false);
            if (second.IsError)
                throw new InvalidOperationException("Expected second report ok: " + second.Error);
            if (child.Status != DysonSessionStatus.Completed)
                throw new InvalidOperationException("Expected second report to mark Completed.");
            if (!string.Equals(child.LastReportSummary, "second handoff", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected LastReportSummary replaced by second handoff.");

            var waited = await waitTask.ConfigureAwait(false);
            if (waited.IsError)
                throw new InvalidOperationException("Expected wait after second report ok: " + waited.Error);
            if (waited.Value.IndexOf("Completed", StringComparison.Ordinal) < 0
                || waited.Value.IndexOf("second handoff", StringComparison.Ordinal) < 0
                || waited.Value.IndexOf("first handoff", StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    "Expected wait JSON Completed with second handoff, not first: " + waited.Value);
            }
        }
        finally
        {
            child.CancelBackgroundRunForTest();
            try
            {
                await child.PromptFinished.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Prompt never started (kickoff failed before hang).
            }
        }
    }

    /// <summary>
    /// Successful Wait marks consume (and suppress) then TryReopenForNewParentTask clears it;
    /// timeout does not mark. Waiting id is set during Wait and cleared after.
    /// </summary>
    private static async Task AssertWaitConsumeMarker()
    {
        var timeoutParent = new StubSession();
        var timeoutChild = new StubSession();
        timeoutParent.RegisterForTest(timeoutChild);

        var timeoutWait = timeoutParent.WaitForSubagentAsync(timeoutChild.Id, timeoutMs: 80);
        await Task.Delay(15).ConfigureAwait(false);
        if (!timeoutParent.IsWaitingOnAnySubagent
            || !timeoutParent.WaitingOnSubagentIds.Contains(timeoutChild.Id)
            || !timeoutParent.ShouldSuppressWaitedCompletionAutoTurn(timeoutChild.Id))
        {
            throw new InvalidOperationException(
                "Expected waiting id + suppress while WaitForSubagent is in flight.");
        }

        if (timeoutParent.HasWaitConsumedCompletion(timeoutChild.Id))
            throw new InvalidOperationException("In-flight Wait must not mark consume until terminal.");

        var timeoutResult = await timeoutWait.ConfigureAwait(false);
        if (timeoutResult.IsError
            || timeoutResult.Value.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Expected timeout JSON: " +
                (timeoutResult.IsError ? timeoutResult.Error : timeoutResult.Value));
        }

        if (timeoutParent.IsWaitingOnAnySubagent
            || timeoutParent.WaitingOnSubagentIds.Contains(timeoutChild.Id)
            || timeoutParent.HasWaitConsumedCompletion(timeoutChild.Id)
            || timeoutParent.ShouldSuppressWaitedCompletionAutoTurn(timeoutChild.Id))
        {
            throw new InvalidOperationException("Timeout must clear waiting id and not mark consume.");
        }

        var parent = new StubSession();
        var child = new StubSession();
        parent.RegisterForTest(child);

        if (!child.TryMarkTerminal(DysonSessionStatus.Completed, "waited handoff"))
            throw new InvalidOperationException("Expected TryMarkTerminal Completed.");

        var waited = await parent.WaitForSubagentAsync(child.Id, timeoutMs: 2000).ConfigureAwait(false);
        if (waited.IsError || waited.Value.IndexOf("Completed", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "Expected successful wait JSON: " + (waited.IsError ? waited.Error : waited.Value));
        }

        if (parent.IsWaitingOnAnySubagent || parent.WaitingOnSubagentIds.Contains(child.Id))
            throw new InvalidOperationException("Expected waiting id cleared after successful Wait.");
        if (!parent.HasWaitConsumedCompletion(child.Id)
            || !parent.ShouldSuppressWaitedCompletionAutoTurn(child.Id))
        {
            throw new InvalidOperationException(
                "Successful Wait must mark HasWaitConsumedCompletion and suppress auto-turn.");
        }

        if (!child.TryReopenForNewParentTask())
            throw new InvalidOperationException("Expected TryReopenForNewParentTask after Completed.");
        if (parent.HasWaitConsumedCompletion(child.Id)
            || parent.ShouldSuppressWaitedCompletionAutoTurn(child.Id))
        {
            throw new InvalidOperationException(
                "TryReopenForNewParentTask must ClearWaitConsumedCompletion on the parent.");
        }
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

    private class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig { AvailableShells = [new DysonConfiguredShellSpec("Cmd", "cmd.exe")] },
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

    /// <summary>
    /// Dedicated child whose PromptHarnessTurnAsync hangs until cancelled so KickOffChildPrompt
    /// cannot mark Failed after reopen (existing StubSession returns Success immediately).
    /// </summary>
    private sealed class HangingChildSession() : StubSession
    {
        public int PromptCount;
        public readonly TaskCompletionSource PromptStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource PromptFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CancelBackgroundRunForTest() => CancelBackgroundRun();

        public override async Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PromptCount);
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
    }
}

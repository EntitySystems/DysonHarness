using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only self-check for session todo TaskCode uniqueness, status enum round-trip,
/// SubmitSubagentReport incomplete-todo gate, Failed-supersede, post-submit reject, EndsCurrentTurn,
/// Failed→Failed reject, TryReopenForNewParentTask resubmit, BeginInFlightPrompt child reopen,
/// success end-turn hint, 2-strike auto-submit, catalog wording, and root catalog omit (Xunit Fact).
/// </summary>
public class DysonSessionTodoTests
{
    [Fact]
    public async Task Run()
    {
        AssertStatusRoundTrip();
        await AssertTaskCodeUniqueness();
        await AssertSubmitSubagentReportTodoGate();
        await AssertSubmitSubagentReportFailedSupersede();
        await AssertSubmitSubagentReportRejectsRetryAfterCompleted();
        await AssertSubmitSubagentReportRejectsFailedRetry();
        await AssertSubmitSubagentReportReopenForNewParentTask();
        await AssertSubmitSubagentReportBeginInFlightPromptReopenAfterCompleted();
        await AssertSubmitSubagentReportSameTurnRetryRejected();
        await AssertSubmitSubagentReportBeginInFlightPromptReopenFailedToFailed();
        AssertSubmitSubagentReportBeginInFlightPromptDoesNotReopenStopped();
        await AssertSubmitSubagentReportBeginInFlightPromptRootNoOp();
        await AssertSubmitSubagentReportSuccessContentEndTurnHint();
        await AssertSubmitSubagentReportEndsCurrentTurn();
        AssertSubmitSubagentReportCatalogWording();
        AssertSubmitSubagentReportRootCatalogOmit();
        await AssertSubmitSubagentReportAutoSubmitOneFailure();
        await AssertSubmitSubagentReportAutoSubmitTwoFailuresCompleted();
        await AssertSubmitSubagentReportAutoSubmitTwoFailuresFailed();
        await AssertSubmitSubagentReportAutoSubmitUnparseable();
        await AssertSubmitSubagentReportAutoSubmitPerTurn();
        await AssertSubmitSubagentReportAutoSubmitExecutorPathUnchanged();
        await AssertSubmitSubagentReportAutoSubmitReopen();
    }

    private static void AssertStatusRoundTrip()
    {
        if ((int)DysonSessionTodoStatus.Pending != 0
            || (int)DysonSessionTodoStatus.Ongoing != 1
            || (int)DysonSessionTodoStatus.Complete != 2)
        {
            throw new InvalidOperationException(
                "DysonSessionTodoStatus ints must be Pending=0, Ongoing=1, Complete=2.");
        }

        foreach (var (raw, expected) in new (string, DysonSessionTodoStatus)[]
                 {
                     ("pending", DysonSessionTodoStatus.Pending),
                     ("Pending", DysonSessionTodoStatus.Pending),
                     ("ONGOING", DysonSessionTodoStatus.Ongoing),
                     ("complete", DysonSessionTodoStatus.Complete),
                 })
        {
            if (!Enum.TryParse(raw, ignoreCase: true, out DysonSessionTodoStatus parsed)
                || !Enum.IsDefined(parsed)
                || parsed != expected)
            {
                throw new InvalidOperationException(
                    $"Status round-trip failed for '{raw}' (got {parsed}).");
            }
        }

        if (Enum.TryParse("bogus", ignoreCase: true, out DysonSessionTodoStatus bogus)
            && Enum.IsDefined(bogus))
        {
            throw new InvalidOperationException("Expected 'bogus' status parse to fail IsDefined.");
        }

        if (Enum.IsDefined((DysonSessionTodoStatus)99))
            throw new InvalidOperationException("Expected status 99 to be undefined.");
    }

    private static async Task AssertTaskCodeUniqueness()
    {
        var session = new StubSession();

        var first = await session.CreateTodoAsync("alpha", "One").ConfigureAwait(false);
        if (first.IsError)
            throw new InvalidOperationException($"Expected create ok, got: {first.Error}");

        var dup = await session.CreateTodoAsync("alpha", "Two").ConfigureAwait(false);
        if (!dup.IsError
            || dup.Error.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                $"Expected duplicate TaskCode error containing 'already exists', got: {(dup.IsError ? dup.Error : "ok")}");
        }

        var trimmedDup = await session.CreateTodoAsync("  alpha  ", "Three").ConfigureAwait(false);
        if (!trimmedDup.IsError)
            throw new InvalidOperationException("Expected trimmed duplicate TaskCode to be rejected.");

        var replaceDup = await session.ReplaceTodosAsync(
            [
                new DysonSessionTodoReplaceItem { TaskCode = "a", DisplayName = "A" },
                new DysonSessionTodoReplaceItem { TaskCode = "a", DisplayName = "B" },
            ]).ConfigureAwait(false);
        if (!replaceDup.IsError
            || replaceDup.Error.IndexOf("Duplicate TaskCode", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                $"Expected replace-set duplicate error, got: {(replaceDup.IsError ? replaceDup.Error : "ok")}");
        }

        var replaceOk = await session.ReplaceTodosAsync(
            [
                new DysonSessionTodoReplaceItem
                {
                    TaskCode = "x",
                    DisplayName = "X",
                    Status = DysonSessionTodoStatus.Ongoing,
                },
            ]).ConfigureAwait(false);
        if (replaceOk.IsError)
            throw new InvalidOperationException($"Expected replace ok, got: {replaceOk.Error}");
        if (replaceOk.Value.Count != 1 || replaceOk.Value[0].Status != DysonSessionTodoStatus.Ongoing)
            throw new InvalidOperationException("Expected replace to keep status Ongoing.");
    }

    private static async Task AssertSubmitSubagentReportTodoGate()
    {
        // Empty todos → success
        var empty = new StubSession();
        var emptyOk = await empty.SubmitSubagentReportAsync("done empty").ConfigureAwait(false);
        if (emptyOk.IsError)
            throw new InvalidOperationException($"Expected empty-todos report ok, got: {emptyOk.Error}");
        if (empty.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected empty-todos session to be Completed.");

        // All complete → success
        var complete = new StubSession();
        var created = await complete.CreateTodoAsync("t1", "One", DysonSessionTodoStatus.Complete)
            .ConfigureAwait(false);
        if (created.IsError)
            throw new InvalidOperationException($"Expected create complete todo ok, got: {created.Error}");
        var completeOk = await complete.SubmitSubagentReportAsync("done all complete").ConfigureAwait(false);
        if (completeOk.IsError)
            throw new InvalidOperationException($"Expected all-complete report ok, got: {completeOk.Error}");
        if (complete.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected all-complete session to be Completed.");

        // Incomplete + completed → error, not terminal
        var blocked = new StubSession();
        var pending = await blocked.CreateTodoAsync("t2", "Two", DysonSessionTodoStatus.Pending)
            .ConfigureAwait(false);
        if (pending.IsError)
            throw new InvalidOperationException($"Expected create pending todo ok, got: {pending.Error}");
        var blockedResult = await blocked.SubmitSubagentReportAsync("should fail").ConfigureAwait(false);
        if (!blockedResult.IsError
            || blockedResult.Error.IndexOf("incomplete todos", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                $"Expected incomplete-todos error, got: {(blockedResult.IsError ? blockedResult.Error : "ok")}");
        }

        if (blocked.IsTerminal)
            throw new InvalidOperationException("Expected blocked session to stay non-terminal.");

        // Incomplete without failed still errors (no skip override)
        var stillBlocked = new StubSession();
        var ongoing = await stillBlocked.CreateTodoAsync("t3", "Three", DysonSessionTodoStatus.Ongoing)
            .ConfigureAwait(false);
        if (ongoing.IsError)
            throw new InvalidOperationException($"Expected create ongoing todo ok, got: {ongoing.Error}");
        var stillBlockedResult = await stillBlocked
            .SubmitSubagentReportAsync("still incomplete")
            .ConfigureAwait(false);
        if (!stillBlockedResult.IsError
            || stillBlockedResult.Error.IndexOf("incomplete todos", StringComparison.OrdinalIgnoreCase) < 0
            || stillBlockedResult.Error.IndexOf("t3", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected incomplete-todos error mentioning t3, got: {(stillBlockedResult.IsError ? stillBlockedResult.Error : "ok")}");
        }

        if (stillBlocked.IsTerminal)
            throw new InvalidOperationException("Expected still-blocked session to stay non-terminal.");

        // Incomplete + failed → success (blocker handoff)
        var failedOk = new StubSession();
        var failedTodo = await failedOk.CreateTodoAsync("t4", "Four", DysonSessionTodoStatus.Pending)
            .ConfigureAwait(false);
        if (failedTodo.IsError)
            throw new InvalidOperationException($"Expected create pending todo ok, got: {failedTodo.Error}");
        var failedReport = await failedOk
            .SubmitSubagentReportAsync("blocked: missing schema", failed: true)
            .ConfigureAwait(false);
        if (failedReport.IsError)
            throw new InvalidOperationException($"Expected failed report with incomplete todos ok, got: {failedReport.Error}");
        if (failedOk.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected incomplete+failed session to be Failed.");
    }

    private static async Task AssertSubmitSubagentReportFailedSupersede()
    {
        // Harness Failed → agent SubmitSubagentReport(completed) supersedes
        var failed = new StubSession();
        if (!failed.TryMarkTerminal(DysonSessionStatus.Failed, "kickoff missed report"))
            throw new InvalidOperationException("Expected TryMarkTerminal Failed to succeed.");

        var supersede = await failed.SubmitSubagentReportAsync("agent handoff").ConfigureAwait(false);
        if (supersede.IsError)
            throw new InvalidOperationException($"Expected Failed supersede ok, got: {supersede.Error}");
        if (failed.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected Failed→Completed supersede.");
        if (!string.Equals(failed.LastReportSummary, "agent handoff", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary replaced on supersede.");

        // Completed → second submit rejected (not idempotent Ok)
        var second = await failed.SubmitSubagentReportAsync("again").ConfigureAwait(false);
        if (!second.IsError
            || second.Error.IndexOf("already submitted", StringComparison.OrdinalIgnoreCase) < 0
            || second.Error.IndexOf(
                "To communicate with the parent without a new report cycle, call TriggerParentEvent instead. After TriggerParentEvent, do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected second submit rejected with already submitted + TriggerParentEvent, got: {(second.IsError ? second.Error : "ok")}");
        }

        if (failed.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected status to stay Completed after rejected retry.");
        if (!string.Equals(failed.LastReportSummary, "agent handoff", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary unchanged on rejected retry.");

        // Stopped → SubmitSubagentReport rejected
        var stopped = new StubSession();
        if (!stopped.TryMarkTerminal(DysonSessionStatus.Stopped, "stopped by parent"))
            throw new InvalidOperationException("Expected TryMarkTerminal Stopped to succeed.");
        var stoppedReport = await stopped.SubmitSubagentReportAsync("should reject").ConfigureAwait(false);
        if (!stoppedReport.IsError
            || stoppedReport.Error.IndexOf("already Stopped", StringComparison.OrdinalIgnoreCase) < 0
            || stoppedReport.Error.IndexOf(
                "To communicate with the parent without a new report cycle, call TriggerParentEvent instead. After TriggerParentEvent, do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected Stopped submit rejected with TriggerParentEvent, got: {(stoppedReport.IsError ? stoppedReport.Error : "ok")}");
        }
    }

    private static async Task AssertSubmitSubagentReportRejectsRetryAfterCompleted()
    {
        var session = new StubSession();
        var first = await session.SubmitSubagentReportAsync("first handoff").ConfigureAwait(false);
        if (first.IsError)
            throw new InvalidOperationException($"Expected first completed report ok, got: {first.Error}");
        if (session.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected first completed report to mark Completed.");

        var retry = await session.SubmitSubagentReportAsync("retry noise").ConfigureAwait(false);
        if (!retry.IsError
            || retry.Error.IndexOf("already submitted", StringComparison.OrdinalIgnoreCase) < 0
            || retry.Error.IndexOf(
                "To communicate with the parent without a new report cycle, call TriggerParentEvent instead. After TriggerParentEvent, do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected post-completed retry rejected with already submitted + TriggerParentEvent, got: {(retry.IsError ? retry.Error : "ok")}");
        }

        if (session.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected status to remain Completed.");
        if (!string.Equals(session.LastReportSummary, "first handoff", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary unchanged on rejected retry.");
    }

    private static async Task AssertSubmitSubagentReportRejectsFailedRetry()
    {
        var session = new StubSession();
        var failed = await session
            .SubmitSubagentReportAsync("blocked: missing schema", failed: true)
            .ConfigureAwait(false);
        if (failed.IsError)
            throw new InvalidOperationException($"Expected first failed report ok, got: {failed.Error}");
        if (session.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected first failed report to mark Failed, not Completed.");
        if (!string.Equals(session.LastReportSummary, "blocked: missing schema", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary to keep failure reason.");

        // Failed → failed again is rejected (lock after terminal handoff)
        var again = await session
            .SubmitSubagentReportAsync("still blocked", failed: true)
            .ConfigureAwait(false);
        if (!again.IsError
            || again.Error.IndexOf("already submitted", StringComparison.OrdinalIgnoreCase) < 0
            || again.Error.IndexOf(
                "To communicate with the parent without a new report cycle, call TriggerParentEvent instead. After TriggerParentEvent, do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected Failed→Failed rejected with already submitted + TriggerParentEvent, got: {(again.IsError ? again.Error : "ok")}");
        }

        if (session.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected re-failed report to stay Failed.");
        if (!string.Equals(session.LastReportSummary, "blocked: missing schema", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary unchanged on rejected Failed retry.");

        // Failed → Completed supersede still allowed
        var supersede = await session.SubmitSubagentReportAsync("recovered handoff").ConfigureAwait(false);
        if (supersede.IsError)
            throw new InvalidOperationException($"Expected Failed→Completed supersede ok, got: {supersede.Error}");
        if (session.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected Failed→Completed supersede after agent failed.");
    }

    private static void AssertSubmitSubagentReportCatalogWording()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("SubmitSubagentReport", out var report))
            throw new InvalidOperationException("SubmitSubagentReport must be in the FullAccess catalog.");

        if (!report.Description.Contains("call ListTodos first", StringComparison.Ordinal)
            || !report.Description.Contains("All session todos must be Complete", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SubmitSubagentReport description must contain 'call ListTodos first' and 'All session todos must be Complete'.");
        }

        if (report.Description.Contains("root Work session", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SubmitSubagentReport description must not contain 'root Work session'.");
        }

        if (!report.Description.Contains("TriggerSubagentEvent", StringComparison.Ordinal)
            || !report.Description.Contains("TriggerParentEvent", StringComparison.Ordinal)
            || !report.Description.Contains("do not call any more tools", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SubmitSubagentReport description must contain 'TriggerSubagentEvent', 'TriggerParentEvent', and 'do not call any more tools'.");
        }

        if (!report.Description.Contains("new child turn", StringComparison.Ordinal)
            || !report.Description.Contains("ShellExited", StringComparison.Ordinal)
            || !report.Description.Contains("ends this turn", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SubmitSubagentReport description must contain 'new child turn', 'ShellExited', and 'ends this turn'.");
        }

        if (!report.Description.Contains("auto-submits the last parseable report", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SubmitSubagentReport description must contain 'auto-submits the last parseable report'.");
        }

        if (DysonAgentSession.MaxSubmitSubagentReportFailuresPerTurn != 2)
        {
            throw new InvalidOperationException(
                $"MaxSubmitSubagentReportFailuresPerTurn must be 2, got {DysonAgentSession.MaxSubmitSubagentReportFailuresPerTurn}.");
        }

        if (!pipeline.Tools.TryGetValue("ListTodos", out var listTodos))
            throw new InvalidOperationException("ListTodos must be in the FullAccess catalog.");

        if (!listTodos.Description.Contains("before SubmitSubagentReport", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ListTodos description must contain 'before SubmitSubagentReport'.");
        }
    }

    private static void AssertSubmitSubagentReportRootCatalogOmit()
    {
        const string tool = "SubmitSubagentReport";
        var config = new DysonAgentSessionConfig();

        foreach (var mode in new[] { DysonAgentModes.Work, DysonAgentModes.Plan, DysonAgentModes.Ask })
        {
            var pipeline = DysonSessionToolsetBuilder.Build(config, mode);
            if (pipeline.Tools.ContainsKey(tool))
                throw new InvalidOperationException($"Root Build({mode}) must omit {tool}.");
        }

        var childPipeline = DysonSessionToolsetBuilder.Build(
            config,
            DysonAgentModes.Work,
            omitRootTaskCompletionTools: true);
        if (!childPipeline.Tools.ContainsKey(tool))
        {
            throw new InvalidOperationException(
                $"Build(omitRootTaskCompletionTools: true) must keep {tool}.");
        }

        var root = new StubSession();
        root.ConfigureRootForTest();
        if (root.McpPipeline.Tools.ContainsKey(tool))
            throw new InvalidOperationException($"ConfigureRootForTest / ConfigureRootInterAgentTools must omit {tool}.");

        var catalog = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!catalog.Tools.ContainsKey(tool))
            throw new InvalidOperationException($"CreateDefault must still contain {tool}.");

        var inAllCatalog = false;
        foreach (var named in DysonSessionToolsetBuilder.AllCatalogTools())
        {
            if (string.Equals(named.Name, tool, StringComparison.Ordinal))
            {
                inAllCatalog = true;
                break;
            }
        }

        if (!inAllCatalog)
            throw new InvalidOperationException($"AllCatalogTools must still contain {tool}.");

        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        if (!child.McpPipeline.Tools.ContainsKey(tool))
            throw new InvalidOperationException($"Registered child catalog must keep {tool}.");
        if (parent.McpPipeline.Tools.ContainsKey(tool))
            throw new InvalidOperationException($"Parent root catalog must still omit {tool} after RegisterForTest.");
    }

    private static async Task AssertSubmitSubagentReportEndsCurrentTurn()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(child, Path.GetTempPath(), http);

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r1",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = """{"summary":"done","status":"completed"}""",
        }).ConfigureAwait(false);

        if (result.IsError)
            throw new InvalidOperationException("SubmitSubagentReport should succeed: " + result.Content);
        if (!result.EndsCurrentTurn)
            throw new InvalidOperationException("First successful SubmitSubagentReport must set EndsCurrentTurn.");
        if (result.Content.IndexOf(
                "Report accepted. Do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "Successful executor SubmitSubagentReport Content must contain the success end-turn hint.");
        }

        if (child.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected session Completed after executor submit.");

        var retry = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r2",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = """{"summary":"again"}""",
        }).ConfigureAwait(false);

        if (!retry.IsError
            || retry.Content.IndexOf("already submitted", StringComparison.OrdinalIgnoreCase) < 0
            || retry.Content.IndexOf(
                "To communicate with the parent without a new report cycle, call TriggerParentEvent instead. After TriggerParentEvent, do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected executor retry error with already submitted + TriggerParentEvent, got: {(retry.IsError ? retry.Content : "ok")}");
        }

        if (retry.EndsCurrentTurn)
            throw new InvalidOperationException("Rejected SubmitSubagentReport must not set EndsCurrentTurn.");
        if (retry.Content.IndexOf(
                "Report accepted. Do not call any more tools; end the turn.",
                StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "Rejected executor SubmitSubagentReport must not contain the success end-turn hint.");
        }

        // Failed handoff also ends the turn
        var failedParent = new StubSession();
        failedParent.ConfigureRootForTest();
        var failedChild = new StubSession();
        failedParent.RegisterForTest(failedChild);
        var failedExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(failedChild, Path.GetTempPath(), http);
        var failedResult = await failedExecutor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r3",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = """{"summary":"blocked","status":"failed"}""",
        }).ConfigureAwait(false);

        if (failedResult.IsError)
            throw new InvalidOperationException("Failed SubmitSubagentReport should succeed: " + failedResult.Content);
        if (!failedResult.EndsCurrentTurn)
            throw new InvalidOperationException("Successful failed-status SubmitSubagentReport must set EndsCurrentTurn.");
        if (failedResult.Content.IndexOf(
                "Report accepted. Do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "Successful failed-status executor Content must contain the success end-turn hint.");
        }

        if (failedChild.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected session Failed after failed-status submit.");
    }

    private static async Task AssertSubmitSubagentReportBeginInFlightPromptReopenAfterCompleted()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var first = await child.SubmitSubagentReportAsync("first handoff").ConfigureAwait(false);
        if (first.IsError)
            throw new InvalidOperationException($"Expected first completed report ok, got: {first.Error}");
        if (child.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected first completed report to mark Completed.");

        using (child.BeginInFlightPrompt(new DysonAgentTurn
               {
                   Kind = DysonAgentTurnKind.Normal,
                   Instruction = "later",
               }))
        {
            if (child.Status != DysonSessionStatus.Active)
            {
                throw new InvalidOperationException(
                    "Expected Status Active after BeginInFlightPrompt on Completed child.");
            }

            var second = await child.SubmitSubagentReportAsync("second handoff").ConfigureAwait(false);
            if (second.IsError)
            {
                throw new InvalidOperationException(
                    $"Expected second completed report after BeginInFlightPrompt ok, got: {second.Error}");
            }

            if (child.Status != DysonSessionStatus.Completed)
                throw new InvalidOperationException("Expected second completed report to mark Completed.");
            if (!string.Equals(child.LastReportSummary, "second handoff", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected LastReportSummary replaced on second completed report.");
        }
    }

    private static async Task AssertSubmitSubagentReportSameTurnRetryRejected()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "same turn",
        };
        using (child.BeginInFlightPrompt(turn))
        {
            var first = await child.SubmitSubagentReportAsync("first handoff").ConfigureAwait(false);
            if (first.IsError)
                throw new InvalidOperationException($"Expected first completed report ok, got: {first.Error}");
            if (child.Status != DysonSessionStatus.Completed)
                throw new InvalidOperationException("Expected first completed report to mark Completed.");

            var retry = await child.SubmitSubagentReportAsync("retry noise").ConfigureAwait(false);
            if (!retry.IsError
                || retry.Error.IndexOf("already submitted", StringComparison.OrdinalIgnoreCase) < 0
                || retry.Error.IndexOf(
                    "To communicate with the parent without a new report cycle, call TriggerParentEvent instead. After TriggerParentEvent, do not call any more tools; end the turn.",
                    StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    $"Expected same-turn retry rejected with already submitted + TriggerParentEvent, got: {(retry.IsError ? retry.Error : "ok")}");
            }

            if (retry.Error.IndexOf(
                    "Report accepted. Do not call any more tools; end the turn.",
                    StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    "Rejected same-turn retry must not contain the success end-turn hint.");
            }

            if (child.Status != DysonSessionStatus.Completed)
                throw new InvalidOperationException("Expected status to stay Completed after same-turn retry.");
            if (!string.Equals(child.LastReportSummary, "first handoff", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected LastReportSummary unchanged on same-turn retry.");
        }
    }

    private static async Task AssertSubmitSubagentReportBeginInFlightPromptReopenFailedToFailed()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var first = await child
            .SubmitSubagentReportAsync("blocked: missing schema", failed: true)
            .ConfigureAwait(false);
        if (first.IsError)
            throw new InvalidOperationException($"Expected first failed report ok, got: {first.Error}");
        if (child.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected first failed report to mark Failed.");

        using (child.BeginInFlightPrompt(new DysonAgentTurn
               {
                   Kind = DysonAgentTurnKind.Normal,
                   Instruction = "later",
               }))
        {
            if (child.Status != DysonSessionStatus.Active)
            {
                throw new InvalidOperationException(
                    "Expected Status Active after BeginInFlightPrompt on Failed child.");
            }

            var second = await child
                .SubmitSubagentReportAsync("still blocked after new turn", failed: true)
                .ConfigureAwait(false);
            if (second.IsError)
            {
                throw new InvalidOperationException(
                    $"Expected second failed report after BeginInFlightPrompt ok, got: {second.Error}");
            }

            if (child.Status != DysonSessionStatus.Failed)
                throw new InvalidOperationException("Expected second failed report after reopen to mark Failed.");
            if (!string.Equals(child.LastReportSummary, "still blocked after new turn", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected LastReportSummary replaced on second failed report.");
        }
    }

    private static void AssertSubmitSubagentReportBeginInFlightPromptDoesNotReopenStopped()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);

        if (!child.TryMarkTerminal(DysonSessionStatus.Stopped, "stopped by parent"))
            throw new InvalidOperationException("Expected TryMarkTerminal Stopped to succeed.");

        using (child.BeginInFlightPrompt(new DysonAgentTurn
               {
                   Kind = DysonAgentTurnKind.Normal,
                   Instruction = "later",
               }))
        {
            if (child.Status != DysonSessionStatus.Stopped)
                throw new InvalidOperationException("Expected Status still Stopped after BeginInFlightPrompt.");
        }
    }

    private static async Task AssertSubmitSubagentReportBeginInFlightPromptRootNoOp()
    {
        var root = new StubSession();
        var first = await root.SubmitSubagentReportAsync("root handoff").ConfigureAwait(false);
        if (first.IsError)
            throw new InvalidOperationException($"Expected root completed report ok, got: {first.Error}");
        if (root.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected root completed report to mark Completed.");

        using (root.BeginInFlightPrompt(new DysonAgentTurn
               {
                   Kind = DysonAgentTurnKind.Normal,
                   Instruction = "later",
               }))
        {
            if (root.Status != DysonSessionStatus.Completed)
            {
                throw new InvalidOperationException(
                    "Expected root (Parent null) to stay Completed after BeginInFlightPrompt.");
            }
        }
    }

    private static async Task AssertSubmitSubagentReportSuccessContentEndTurnHint()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var ok = await child.SubmitSubagentReportAsync("done empty").ConfigureAwait(false);
        if (ok.IsError)
            throw new InvalidOperationException($"Expected completed report ok, got: {ok.Error}");
        if (ok.Value.IndexOf(
                "Report accepted. Do not call any more tools; end the turn.",
                StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "Expected accepted Value to contain the success end-turn hint.");
        }

        if (ok.Value.IndexOf("subagentId", StringComparison.Ordinal) < 0
            || ok.Value.IndexOf("persistenceId", StringComparison.Ordinal) < 0
            || ok.Value.IndexOf("status", StringComparison.Ordinal) < 0
            || ok.Value.IndexOf("summary", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException($"Expected accepted JSON fields in Value, got: {ok.Value}");
        }

        var retry = await child.SubmitSubagentReportAsync("again").ConfigureAwait(false);
        if (!retry.IsError
            || retry.Error.IndexOf("already submitted", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                $"Expected retry after completed to fail, got: {(retry.IsError ? retry.Error : "ok")}");
        }

        if (retry.Error.IndexOf(
                "Report accepted. Do not call any more tools; end the turn.",
                StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "Rejected retry must not contain the success end-turn hint.");
        }
    }

    private static async Task AssertSubmitSubagentReportReopenForNewParentTask()
    {
        // Completed → reopen → second completed report succeeds with the new summary.
        var completed = new StubSession();
        var first = await completed.SubmitSubagentReportAsync("first handoff").ConfigureAwait(false);
        if (first.IsError)
            throw new InvalidOperationException($"Expected first completed report ok, got: {first.Error}");
        if (completed.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected first completed report to mark Completed.");

        if (!completed.TryReopenForNewParentTask())
            throw new InvalidOperationException("Expected TryReopenForNewParentTask true after Completed.");
        if (completed.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected Status Active after reopen from Completed.");
        if (completed.IsTerminal)
            throw new InvalidOperationException("Expected reopened session to be non-terminal.");
        if (!string.Equals(completed.LastReportSummary, "first handoff", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary kept after reopen from Completed.");

        var second = await completed.SubmitSubagentReportAsync("second handoff").ConfigureAwait(false);
        if (second.IsError)
            throw new InvalidOperationException($"Expected second completed report after reopen ok, got: {second.Error}");
        if (completed.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected second completed report to mark Completed.");
        if (!string.Equals(completed.LastReportSummary, "second handoff", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary replaced on second completed report.");

        // Failed → reopen → second failed report succeeds (new cycle; without reopen Failed→Failed rejects).
        var failed = new StubSession();
        var failedFirst = await failed
            .SubmitSubagentReportAsync("blocked: missing schema", failed: true)
            .ConfigureAwait(false);
        if (failedFirst.IsError)
            throw new InvalidOperationException($"Expected first failed report ok, got: {failedFirst.Error}");
        if (failed.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected first failed report to mark Failed.");

        if (!failed.TryReopenForNewParentTask())
            throw new InvalidOperationException("Expected TryReopenForNewParentTask true after Failed.");
        if (failed.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected Status Active after reopen from Failed.");
        if (!string.Equals(failed.LastReportSummary, "blocked: missing schema", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary kept after reopen from Failed.");

        var failedSecond = await failed
            .SubmitSubagentReportAsync("still blocked after reopen", failed: true)
            .ConfigureAwait(false);
        if (failedSecond.IsError)
            throw new InvalidOperationException($"Expected second failed report after reopen ok, got: {failedSecond.Error}");
        if (failed.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected second failed report after reopen to mark Failed.");
        if (!string.Equals(failed.LastReportSummary, "still blocked after reopen", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary replaced on second failed report.");

        // Reopen is a no-op on already-Active.
        var active = new StubSession();
        if (active.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected new StubSession to start Active.");
        if (active.TryReopenForNewParentTask())
            throw new InvalidOperationException("Expected TryReopenForNewParentTask false on Active.");
        if (active.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected Status unchanged after reopen no-op on Active.");

        // Reopen is a no-op on Stopped.
        var stopped = new StubSession();
        if (!stopped.TryMarkTerminal(DysonSessionStatus.Stopped, "stopped by parent"))
            throw new InvalidOperationException("Expected TryMarkTerminal Stopped to succeed.");
        if (stopped.TryReopenForNewParentTask())
            throw new InvalidOperationException("Expected TryReopenForNewParentTask false on Stopped.");
        if (stopped.Status != DysonSessionStatus.Stopped)
            throw new InvalidOperationException("Expected Status unchanged after reopen no-op on Stopped.");

        // Reopen is a no-op on Interrupted.
        var interrupted = new StubSession();
        if (!interrupted.TryMarkTerminal(DysonSessionStatus.Interrupted, "interrupted"))
            throw new InvalidOperationException("Expected TryMarkTerminal Interrupted to succeed.");
        if (interrupted.TryReopenForNewParentTask())
            throw new InvalidOperationException("Expected TryReopenForNewParentTask false on Interrupted.");
        if (interrupted.Status != DysonSessionStatus.Interrupted)
            throw new InvalidOperationException("Expected Status unchanged after reopen no-op on Interrupted.");
    }

    private static async Task AssertSubmitSubagentReportAutoSubmitOneFailure()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        var pending = await child.CreateTodoAsync("t1", "One").ConfigureAwait(false);
        if (pending.IsError)
            throw new InvalidOperationException($"Expected pending todo ok, got: {pending.Error}");

        var turn = NewNormalTurn();
        EnqueueFailedSubmit(turn, "c1", """{"summary":"findings here"}""");

        var auto = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turn).ConfigureAwait(false);
        if (auto)
            throw new InvalidOperationException("Expected 1 failed submit not to auto-submit.");
        if (child.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected child to stay Active after 1 failed submit.");
        if (parent.TryDequeueInterrupt(out _))
            throw new InvalidOperationException("Expected no parent interrupt after 1 failed submit.");
    }

    private static async Task AssertSubmitSubagentReportAutoSubmitTwoFailuresCompleted()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        var pending = await child.CreateTodoAsync("t1", "One").ConfigureAwait(false);
        if (pending.IsError)
            throw new InvalidOperationException($"Expected pending todo ok, got: {pending.Error}");

        var turn = NewNormalTurn();
        EnqueueFailedSubmit(turn, "c1", """{"summary":"findings here"}""");
        EnqueueFailedSubmit(turn, "c2", """{"summary":"findings here"}""");

        var auto = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turn).ConfigureAwait(false);
        if (!auto)
            throw new InvalidOperationException("Expected 2 failed submits to auto-submit.");
        if (child.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected auto-submit with completed args to mark Completed (bypass incomplete todos), not Stopped.");
        if (child.LastReportSummary is null
            || child.LastReportSummary.IndexOf("findings here", StringComparison.Ordinal) < 0
            || !child.LastReportSummary.Contains(DysonAgentSession.AutoSubmitSubagentReportHarnessNote, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected LastReportSummary to keep original summary plus harness note, got: {child.LastReportSummary}");
        }

        if (!parent.TryDequeueInterrupt(out var interrupt)
            || interrupt.Kind != DysonAgentInterruptKind.SubagentCompleted
            || interrupt.Summary is null
            || interrupt.Summary.IndexOf("findings here", StringComparison.Ordinal) < 0
            || !interrupt.Summary.Contains(DysonAgentSession.AutoSubmitSubagentReportHarnessNote, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected parent SubagentCompleted with original summary + harness note, got: {(interrupt is null ? "none" : $"{interrupt.Kind}: {interrupt.Summary}")}");
        }
    }

    private static async Task AssertSubmitSubagentReportAutoSubmitTwoFailuresFailed()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        var pending = await child.CreateTodoAsync("t1", "One").ConfigureAwait(false);
        if (pending.IsError)
            throw new InvalidOperationException($"Expected pending todo ok, got: {pending.Error}");

        var turn = NewNormalTurn();
        EnqueueFailedSubmit(turn, "c1", """{"summary":"findings here"}""");
        EnqueueFailedSubmit(turn, "c2", """{"summary":"blocked","status":"failed"}""");

        var auto = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turn).ConfigureAwait(false);
        if (!auto)
            throw new InvalidOperationException("Expected 2 failed submits to auto-submit.");
        if (child.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected last parseable failed-status args to mark Failed.");
        if (child.LastReportSummary is null
            || child.LastReportSummary.IndexOf("blocked", StringComparison.Ordinal) < 0
            || !child.LastReportSummary.Contains(DysonAgentSession.AutoSubmitSubagentReportHarnessNote, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected LastReportSummary to keep blocked + harness note, got: {child.LastReportSummary}");
        }

        if (!parent.TryDequeueInterrupt(out var interrupt)
            || interrupt.Kind != DysonAgentInterruptKind.SubagentFailed)
        {
            throw new InvalidOperationException(
                $"Expected parent SubagentFailed, got: {(interrupt is null ? "none" : interrupt.Kind.ToString())}");
        }
    }

    private static async Task AssertSubmitSubagentReportAutoSubmitUnparseable()
    {
        await AssertUnparseableAutoFail("{not json").ConfigureAwait(false);
        await AssertUnparseableAutoFail("{}").ConfigureAwait(false);
    }

    private static async Task AssertUnparseableAutoFail(string argumentsJson)
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var turn = NewNormalTurn();
        EnqueueFailedSubmit(turn, "c1", argumentsJson);
        EnqueueFailedSubmit(turn, "c2", argumentsJson);

        var auto = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turn).ConfigureAwait(false);
        if (!auto)
            throw new InvalidOperationException("Expected 2 unparseable failed submits to auto-fail.");
        if (child.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected unparseable auto-submit to mark Failed.");

        const string expected =
            "Harness auto-failed after 2 failed SubmitSubagentReport calls this turn (no parseable summary).";
        if (!string.Equals(child.LastReportSummary, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected unparseable harness message, got: {child.LastReportSummary}");
        }

        if (!parent.TryDequeueInterrupt(out var interrupt)
            || interrupt.Kind != DysonAgentInterruptKind.SubagentFailed
            || !string.Equals(interrupt.Summary, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected parent SubagentFailed with unparseable message, got: {(interrupt is null ? "none" : $"{interrupt.Kind}: {interrupt.Summary}")}");
        }
    }

    private static async Task AssertSubmitSubagentReportAutoSubmitPerTurn()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        var pending = await child.CreateTodoAsync("t1", "One").ConfigureAwait(false);
        if (pending.IsError)
            throw new InvalidOperationException($"Expected pending todo ok, got: {pending.Error}");

        var turnA = NewNormalTurn();
        EnqueueFailedSubmit(turnA, "a1", """{"summary":"findings here"}""");
        var first = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turnA).ConfigureAwait(false);
        if (first)
            throw new InvalidOperationException("Expected turn A with 1 fail not to auto-submit.");
        if (child.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected child Active after turn A 1 fail.");

        var turnB = NewNormalTurn();
        EnqueueFailedSubmit(turnB, "b1", """{"summary":"findings here"}""");
        var second = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turnB).ConfigureAwait(false);
        if (second)
            throw new InvalidOperationException("Expected turn B with 1 fail not to auto-submit (strikes are per-turn).");
        if (child.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected child Active after turn B 1 fail.");

        EnqueueFailedSubmit(turnB, "b2", """{"summary":"findings here"}""");
        var third = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turnB).ConfigureAwait(false);
        if (!third)
            throw new InvalidOperationException("Expected turn B with 2 fails to auto-submit.");
        if (child.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected Completed after turn B 2 fails.");
    }

    private static async Task AssertSubmitSubagentReportAutoSubmitExecutorPathUnchanged()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        var pending = await child.CreateTodoAsync("t1", "One").ConfigureAwait(false);
        if (pending.IsError)
            throw new InvalidOperationException($"Expected pending todo ok, got: {pending.Error}");

        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(child, Path.GetTempPath(), http);
        const string args = """{"summary":"findings here"}""";

        var first = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "e1",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = args,
        }).ConfigureAwait(false);
        if (!first.IsError || first.EndsCurrentTurn)
        {
            throw new InvalidOperationException(
                $"Expected first executor submit with pending todos IsError and no EndsCurrentTurn, got: error={first.IsError} ends={first.EndsCurrentTurn} {first.Content}");
        }

        var second = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "e2",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = args,
        }).ConfigureAwait(false);
        if (!second.IsError || second.EndsCurrentTurn)
        {
            throw new InvalidOperationException(
                $"Expected 2nd executor submit still IsError (auto-accept is harness-side), got: error={second.IsError} ends={second.EndsCurrentTurn} {second.Content}");
        }

        var turn = NewNormalTurn();
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "e1",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = args,
        });
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "e2",
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = args,
        });
        turn.ResponseLog.Enqueue(first);
        turn.ResponseLog.Enqueue(second);

        var auto = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turn).ConfigureAwait(false);
        if (!auto)
            throw new InvalidOperationException("Expected helper to auto-accept after 2 executor failures.");
        if (child.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected harness-side auto-accept to mark Completed.");

        var rows = turn.ResponseLog.ToArray();
        if (rows.Length != 2 || !rows[0].IsError || !rows[1].IsError || rows[1].EndsCurrentTurn)
        {
            throw new InvalidOperationException(
                "Expected both ResponseLog rows to stay IsError (2nd row is not a successful third tool call).");
        }
    }

    private static async Task AssertSubmitSubagentReportAutoSubmitReopen()
    {
        var parent = new StubSession();
        parent.ConfigureRootForTest();
        var child = new StubSession();
        parent.RegisterForTest(child);
        var pending = await child.CreateTodoAsync("t1", "One").ConfigureAwait(false);
        if (pending.IsError)
            throw new InvalidOperationException($"Expected pending todo ok, got: {pending.Error}");

        var turn = NewNormalTurn();
        EnqueueFailedSubmit(turn, "c1", """{"summary":"findings here"}""");
        EnqueueFailedSubmit(turn, "c2", """{"summary":"findings here"}""");
        var auto = await child.TryAutoSubmitAfterRepeatedFailuresAsync(turn).ConfigureAwait(false);
        if (!auto || child.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected 2-fail auto-complete before reopen.");

        if (!child.TryReopenForNewParentTask())
            throw new InvalidOperationException("Expected TryReopenForNewParentTask true after 2-fail auto-complete (not Stopped).");
        if (child.Status != DysonSessionStatus.Active)
            throw new InvalidOperationException("Expected Status Active after reopen from auto-complete.");
    }

    private static DysonAgentTurn NewNormalTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "x",
            StartedUtc = DateTime.UtcNow,
        };

    private static void EnqueueFailedSubmit(DysonAgentTurn turn, string callId, string argumentsJson)
    {
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = callId,
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            ArgumentsJson = argumentsJson,
        });
        turn.ResponseLog.Enqueue(new DysonToolCallResult
        {
            CallId = callId,
            ToolName = "SubmitSubagentReport",
            Stage = 0,
            IsError = true,
            Content = "incomplete todos",
        });
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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

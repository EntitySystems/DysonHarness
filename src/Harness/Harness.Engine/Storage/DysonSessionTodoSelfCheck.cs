namespace DysonHarness;

/// <summary>
/// ponytail: assert-only self-check for session todo TaskCode uniqueness, status enum round-trip,
/// SubmitSubagentReport incomplete-todo gate, Failed-supersede, idempotent Completed retry,
/// and first-failed stays Failed (no test framework).
/// Run: <c>DysonSessionTodoSelfCheck.Run()</c> (also from UI <c>Program</c> startup).
/// </summary>
public static class DysonSessionTodoSelfCheck
{
    public static void Run()
    {
        AssertStatusRoundTrip();
        AssertTaskCodeUniqueness().GetAwaiter().GetResult();
        AssertSubmitSubagentReportTodoGate().GetAwaiter().GetResult();
        AssertSubmitSubagentReportFailedSupersede().GetAwaiter().GetResult();
        AssertSubmitSubagentReportIdempotentCompleted().GetAwaiter().GetResult();
        AssertSubmitSubagentReportFirstFailedStaysFailed().GetAwaiter().GetResult();
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

        // Incomplete without skip → error, not terminal
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

        // Incomplete with skipTasksCheck → success + incompleteTodos
        var skipped = new StubSession();
        var ongoing = await skipped.CreateTodoAsync("t3", "Three", DysonSessionTodoStatus.Ongoing)
            .ConfigureAwait(false);
        if (ongoing.IsError)
            throw new InvalidOperationException($"Expected create ongoing todo ok, got: {ongoing.Error}");
        var skipOk = await skipped
            .SubmitSubagentReportAsync("forced", skipTasksCheck: true)
            .ConfigureAwait(false);
        if (skipOk.IsError)
            throw new InvalidOperationException($"Expected skipTasksCheck report ok, got: {skipOk.Error}");
        if (skipped.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected skipTasksCheck session to be Completed.");
        if (skipOk.Value.IndexOf("incompleteTodos", StringComparison.Ordinal) < 0
            || skipOk.Value.IndexOf("\"skipTasksCheck\":true", StringComparison.Ordinal) < 0
            || skipOk.Value.IndexOf("t3", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected skip payload to include incompleteTodos/skipTasksCheck/t3, got: {skipOk.Value}");
        }
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

        // Completed → second submit is idempotent success (no error)
        var second = await failed.SubmitSubagentReportAsync("again").ConfigureAwait(false);
        if (second.IsError)
            throw new InvalidOperationException($"Expected second submit idempotent ok, got: {second.Error}");
        if (second.Value.IndexOf("\"idempotent\":true", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException($"Expected idempotent:true in second submit, got: {second.Value}");
        if (failed.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected status to stay Completed after idempotent retry.");
        if (!string.Equals(failed.LastReportSummary, "agent handoff", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary unchanged on idempotent retry.");

        // Stopped → SubmitSubagentReport rejected
        var stopped = new StubSession();
        if (!stopped.TryMarkTerminal(DysonSessionStatus.Stopped, "stopped by parent"))
            throw new InvalidOperationException("Expected TryMarkTerminal Stopped to succeed.");
        var stoppedReport = await stopped.SubmitSubagentReportAsync("should reject").ConfigureAwait(false);
        if (!stoppedReport.IsError
            || stoppedReport.Error.IndexOf("already Stopped", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                $"Expected Stopped submit rejected, got: {(stoppedReport.IsError ? stoppedReport.Error : "ok")}");
        }
    }

    private static async Task AssertSubmitSubagentReportIdempotentCompleted()
    {
        var session = new StubSession();
        var first = await session.SubmitSubagentReportAsync("first handoff").ConfigureAwait(false);
        if (first.IsError)
            throw new InvalidOperationException($"Expected first completed report ok, got: {first.Error}");
        if (session.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected first completed report to mark Completed.");

        var retry = await session.SubmitSubagentReportAsync("retry noise").ConfigureAwait(false);
        if (retry.IsError)
            throw new InvalidOperationException($"Expected idempotent retry ok, got: {retry.Error}");
        if (retry.Value.IndexOf("\"idempotent\":true", StringComparison.Ordinal) < 0
            || retry.Value.IndexOf("first handoff", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                $"Expected idempotent payload with original summary, got: {retry.Value}");
        }

        if (session.Status != DysonSessionStatus.Completed)
            throw new InvalidOperationException("Expected status to remain Completed.");
        if (!string.Equals(session.LastReportSummary, "first handoff", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected LastReportSummary unchanged on idempotent retry.");
    }

    private static async Task AssertSubmitSubagentReportFirstFailedStaysFailed()
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

        // Failed + failed again is not the post-success idempotent path (still Failed, not flipped to Completed)
        var again = await session
            .SubmitSubagentReportAsync("still blocked", failed: true)
            .ConfigureAwait(false);
        if (again.IsError)
            throw new InvalidOperationException($"Expected Failed→Failed re-report ok, got: {again.Error}");
        if (session.Status != DysonSessionStatus.Failed)
            throw new InvalidOperationException("Expected re-failed report to stay Failed.");
        if (again.Value.IndexOf("\"idempotent\":true", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Did not expect idempotent:true on Failed re-report.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

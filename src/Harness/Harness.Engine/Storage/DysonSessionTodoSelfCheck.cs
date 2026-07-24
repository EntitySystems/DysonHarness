namespace DysonHarness;

/// <summary>
/// ponytail: assert-only self-check for session todo TaskCode uniqueness, status enum round-trip,
/// and SubmitSubagentReport incomplete-todo gate (no test framework).
/// Run: <c>DysonSessionTodoSelfCheck.Run()</c> (also from UI <c>Program</c> startup).
/// </summary>
public static class DysonSessionTodoSelfCheck
{
    public static void Run()
    {
        AssertStatusRoundTrip();
        AssertTaskCodeUniqueness().GetAwaiter().GetResult();
        AssertSubmitSubagentReportTodoGate().GetAwaiter().GetResult();
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

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

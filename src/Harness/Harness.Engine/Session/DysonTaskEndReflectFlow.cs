namespace DysonHarness;

/// <summary>
/// Creates the task-end reflection turn used when a session still has incomplete todos.
/// Root lifecycle evaluation is host-driven; child eligibility is evaluated by
/// <see cref="TryCreateForChild"/> from <see cref="DysonAgentSession.ApplyChildReportWatchAsync"/>,
/// before the watch's reminders and synthesized failed report.
/// </summary>
public static class DysonTaskEndReflectFlow
{
    private const int MaxSnapshotTodos = 20;

    private const string Instruction = """
        The prior turn completed. No subagents are running. One or more todos are still pending or ongoing.

        Review the pending work, perform and verify what remains, and update todo status accurately rather than declaring success prematurely.
        Do not claim completion while required work is unfinished.

        Check each incomplete todo against evidence in this session (files, tests, tool results). Mark complete only what is actually done; keep pending or ongoing items honest.
        """;

    /// <summary>Child sessions: the turn ended idle with open todos; the model picks the next step.</summary>
    public const string ChildInstruction = """
        Your turn ended with open todos, no subagent running, and nothing else scheduled. The session will sit idle unless you act now. Choose one:
        1. Continue the remaining work yourself.
        2. Start a subagent for the remaining piece.
        3. Call SubmitSubagentReport: completed if the work is done, blocked if something outside you blocks it, failed if it cannot continue.

        Check each open todo below against evidence in this session (files, commits, tool results). If a todo is done, mark it complete with a comment saying how it was verified. If it no longer applies, mark it complete with a comment saying why. A completed report requires every todo to be complete. Do not mark work complete that you did not do.
        """;

    /// <summary>Creates a TaskEndReflect turn with a compact incomplete-todo snapshot.</summary>
    public static DysonAgentTurn CreateTurn(IReadOnlyList<DysonSessionTodo> todos) =>
        CreateTurn(todos, Instruction);

    private static DysonAgentTurn CreateTurn(IReadOnlyList<DysonSessionTodo> todos, string text)
    {
        ArgumentNullException.ThrowIfNull(todos);

        var snapshot = FormatIncompleteTodoSnapshot(todos);
        var instruction = string.IsNullOrEmpty(snapshot)
            ? text
            : $"{text}\n\n## Incomplete todos\n{snapshot}";

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.TaskEndReflect,
            Instruction = instruction,
            StartedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Returns a child reflection only after a completed eligible turn leaves incomplete work
    /// and no ordinary follow-up/descendant remains. A reflection itself never retriggers one.
    /// Never while a summary runs, a parent message is held, or a parent-event reply is pending;
    /// never for the voice sidecar.
    /// </summary>
    public static bool TryCreateForChild(DysonAgentSession child, out DysonAgentTurn? reflection)
    {
        ArgumentNullException.ThrowIfNull(child);
        reflection = null;

        if (child.Parent is null
            || child.IsVoiceSidecar
            || child.IsTerminal
            || child.Status != DysonSessionStatus.Active
            || child.HasPendingTurn
            || child.InFlightPromptTurn is not null
            || child.IsAwaitingDescendants
            || child.HeldInterruptCount > 0
            || child.IsSummarizingForInterrupt
            || child.HasPendingParentEventWait
            || child.Turns.Count == 0)
        {
            return false;
        }

        // Not CompletedUtc: KickOff child turns are only stamped when a host persists them, and
        // InFlightPromptTurn is already null here, so the last turn has ended.
        var last = child.Turns[^1];
        if (child.IsTurnInFlight(last.Id)
            || !DysonTaskLifecycleFlow.IsTaskEndReflectionTriggerKind(last.Kind)
            || last.Kind == DysonAgentTurnKind.TaskEndReflect)
        {
            return false;
        }

        var hasIncomplete = child.Todos.Any(todo =>
            todo.Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing);
        if (!hasIncomplete)
            return false;

        reflection = CreateTurn(child.Todos, ChildInstruction);
        return true;
    }

    private static string FormatIncompleteTodoSnapshot(IReadOnlyList<DysonSessionTodo> todos)
    {
        var items = todos
            .Where(todo => todo.Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing)
            .Take(MaxSnapshotTodos)
            .Select(todo => $"- `{todo.TaskCode}` — {todo.DisplayName} ({todo.Status})")
            .ToList();

        var omitted = todos.Count(todo =>
            todo.Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing) - items.Count;
        if (omitted > 0)
            items.Add($"- …and {omitted} more incomplete todo(s).");

        return string.Join("\n", items);
    }
}

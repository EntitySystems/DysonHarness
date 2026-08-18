namespace DysonHarness;

/// <summary>
/// Creates the task-end reflection turn used when a session still has incomplete todos.
/// Root lifecycle evaluation is host-driven; child eligibility is evaluated by
/// <see cref="TryCreateForChild"/> immediately before the ordinary missing-report failure path.
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

    /// <summary>Creates a TaskEndReflect turn with a compact incomplete-todo snapshot.</summary>
    public static DysonAgentTurn CreateTurn(IReadOnlyList<DysonSessionTodo> todos)
    {
        ArgumentNullException.ThrowIfNull(todos);

        var snapshot = FormatIncompleteTodoSnapshot(todos);
        var instruction = string.IsNullOrEmpty(snapshot)
            ? Instruction
            : $"{Instruction}\n\n## Incomplete todos\n{snapshot}";

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
    /// </summary>
    public static bool TryCreateForChild(DysonAgentSession child, out DysonAgentTurn? reflection)
    {
        ArgumentNullException.ThrowIfNull(child);
        reflection = null;

        if (child.Parent is null
            || child.IsTerminal
            || child.Status != DysonSessionStatus.Active
            || child.HasPendingTurn
            || child.InFlightPromptTurn is not null
            || DysonTaskLifecycleFlow.HasActiveDescendant(child)
            || child.Turns.Count == 0)
        {
            return false;
        }

        var last = child.Turns[^1];
        if (last.CompletedUtc is null
            || !DysonTaskLifecycleFlow.IsTaskEndReflectionTriggerKind(last.Kind)
            || last.Kind == DysonAgentTurnKind.TaskEndReflect)
        {
            return false;
        }

        var hasIncomplete = child.Todos.Any(todo =>
            todo.Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing);
        if (!hasIncomplete)
            return false;

        reflection = CreateTurn(child.Todos);
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

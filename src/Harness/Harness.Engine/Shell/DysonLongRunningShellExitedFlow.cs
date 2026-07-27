namespace DysonHarness;

/// <summary>
/// ShellExited harness turn: locked Instruction with auto-read tail, then trim after completion.
/// </summary>
public static class DysonLongRunningShellExitedFlow
{
    public const string TailSectionMarker = "**Shell output (auto-read tail)**";

    public const int DefaultIncludeTailMaxChars = 8000;

    /// <summary>
    /// Maps terminal shell state → outcome string for the Instruction / interrupt.
    /// Soft-cancel then exit counts as <c>cancelled</c>.
    /// </summary>
    public static string MapOutcome(
        DysonLongRunningShellStatus status,
        int? exitCode,
        bool wasCancelRequested = false)
    {
        if (status == DysonLongRunningShellStatus.Aborted || wasCancelRequested)
            return "cancelled";

        if (status == DysonLongRunningShellStatus.Exited && exitCode is 0)
            return "success";

        return "failure";
    }

    public static string BuildInstruction(
        int id,
        string outcome,
        int? exitCode,
        string shell,
        string command,
        string cwd,
        string? tail)
    {
        var exitCodeText = exitCode is int code ? code.ToString() : "(unknown)";
        var tailText = string.IsNullOrWhiteSpace(tail) ? "(no output captured)" : tail.TrimEnd();

        return
            $"""
            # Shell exited

            A long-running shell you subscribed to has finished. This turn is for organizing that result and deciding what to do next.

            - longRunningShellId: {id}
            - outcome: {outcome}
            - exitCode: {exitCodeText}
            - shell: {shell}
            - command: {command}
            - cwd: {cwd}

            {TailSectionMarker}

            {tailText}

            Your deliverable this turn:

            1. **Organize the result** — Briefly classify what happened (success / failure / cancelled) using the outcome and the output above. Call out the important signals (errors, failing tests, build warnings, ready URL/port, blockers).
            2. **Derive tasks** — Produce a concrete checklist of follow-up work based on that output (fix failures, re-run subsets, file changes, verification, or next orchestration steps). Prefer session todos (`CreateTodo` / `UpdateTodo`) when the work spans multiple steps.
            3. **Act** — Start the highest-priority next step in this turn when it is clear; otherwise stop after the organized result + task list and wait for the user.

            Do not re-read the whole shell log unless the auto-read tail is clearly truncated and you need more context (`ReadLongRunningShellTail`). Do not restart the same long-running process unless the tasks require it.
            """;
    }

    public static string BuildTrimmedInstruction(int id, string outcome, int? exitCode)
    {
        var exitCodeText = exitCode is int code ? code.ToString() : "(unknown)";
        return
            $"""
            # Shell exited

            A long-running shell you subscribed to finished (outcome: {outcome}, exitCode: {exitCodeText}, id: {id}).
            Shell output was auto-read and reviewed in this turn; the raw tail was trimmed from history to reduce context noise.
            """;
    }

    /// <summary>
    /// Creates a <see cref="DysonAgentTurnKind.ShellExited"/> turn with auto-read tail already in Instruction.
    /// </summary>
    public static DysonAgentTurn CreateTurn(
        int id,
        string outcome,
        int? exitCode,
        string shell,
        string command,
        string cwd,
        string? tail)
    {
        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.ShellExited,
            Instruction = BuildInstruction(id, outcome, exitCode, shell, command, cwd, tail),
            StartedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Creates a ShellExited turn from interrupt + registry snapshot + pre-read tail.
    /// </summary>
    public static DysonAgentTurn CreateTurn(
        DysonAgentInterrupt interrupt,
        DysonLongRunningShellInfo info,
        string? tail)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        ArgumentNullException.ThrowIfNull(info);

        var outcome = string.IsNullOrWhiteSpace(interrupt.ShellOutcome)
            ? MapOutcome(info.Status, info.ExitCode)
            : interrupt.ShellOutcome.Trim();

        return CreateTurn(
            info.Id,
            outcome,
            interrupt.ExitCode ?? info.ExitCode,
            info.ShellName,
            info.Command,
            info.WorkingDirectory,
            tail);
    }

    /// <summary>
    /// Drops the auto-read tail section from Instruction after the turn finishes (transcript hygiene).
    /// </summary>
    public static void TrimInstructionAfterCompletion(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.Kind != DysonAgentTurnKind.ShellExited)
            return;

        var instruction = turn.Instruction;
        if (string.IsNullOrEmpty(instruction))
            return;

        // Prefer ids/outcome already in the full Instruction; fall back to trimmed placeholder.
        var id = TryParseId(instruction) ?? 0;
        var outcome = TryParseField(instruction, "outcome") ?? "unknown";
        var exitCodeText = TryParseField(instruction, "exitCode");
        int? exitCode = int.TryParse(exitCodeText, out var code) ? code : null;

        turn.Instruction = BuildTrimmedInstruction(id, outcome, exitCode);
    }

    private static int? TryParseId(string instruction)
    {
        const string marker = "longRunningShellId: ";
        var idx = instruction.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var start = idx + marker.Length;
        var end = start;
        while (end < instruction.Length && char.IsDigit(instruction[end]))
            end++;
        return int.TryParse(instruction.AsSpan(start, end - start), out var id) ? id : null;
    }

    private static string? TryParseField(string instruction, string name)
    {
        var marker = $"- {name}: ";
        var idx = instruction.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var start = idx + marker.Length;
        var end = instruction.IndexOfAny(['\r', '\n'], start);
        if (end < 0)
            end = instruction.Length;
        return instruction[start..end].Trim();
    }
}

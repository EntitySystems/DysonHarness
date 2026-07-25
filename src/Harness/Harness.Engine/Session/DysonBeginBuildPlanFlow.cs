namespace DysonHarness;

/// <summary>
/// Factories and instruction text for BeginBuildPlan harness turns (composer Build plan).
/// </summary>
public static class DysonBeginBuildPlanFlow
{
    /// <summary>
    /// Normal-turn prompt the host enqueues after a successful BeginBuildPlan so Work
    /// continues without a manual user message.
    /// </summary>
    public const string ContinuationPrompt =
        "Continue the plan implementation as per previous instructions";

    /// <summary>
    /// Whether the host should enqueue <see cref="ContinuationPrompt"/> after this turn
    /// completes successfully. True only for <see cref="DysonAgentTurnKind.BeginBuildPlan"/>.
    /// </summary>
    public static bool ShouldEnqueueBuildContinuation(DysonAgentTurnKind kind) =>
        kind == DysonAgentTurnKind.BeginBuildPlan;

    /// <summary>
    /// Harness mandate: layout-only turn — read the plan, emit Recap + Agent actions.
    /// No tools / implementation this turn; the host enqueues a Normal continuation next.
    /// Optional Explore report blocks are folded into the layout when Plan-mode buffered them.
    /// </summary>
    public static string BuildInstruction(
        string planRelativePath,
        IReadOnlyList<string>? reportBlocks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planRelativePath);
        var path = planRelativePath.Trim().Replace('\\', '/');

        // Chat renders Instruction as markdown: one # title, bold labels — not ## sections
        // with long prose. Reply section names stay in backticks so they don't render as H2.
        var instruction = $"""
            # Begin build plan

            Begin build of the published plan at `{path}`.
            Read that plan file first.

            This turn is layout-only. Your only deliverable is a reply with exactly these markdown sections:

            - **`## Recap`** — Brief restatement of the plan goal and constraints (enough for later turns without re-reading the whole file).
            - **`## Agent actions`** — An ordered, concrete checklist of who/what comes next (e.g. Drone briefs, solution setup, verify steps).

            Do not call tools this turn: no `StartSubagent` / Drones, no `WriteFile`, no shell, no product work.
            Layout the functional instructions only; do not invent a new plan file.
            The next harness turn will automatically continue and run the implementation from that Agent actions set.
            """;

        if (reportBlocks is null || reportBlocks.Count == 0)
            return instruction;

        var parts = new List<string>(1 + reportBlocks.Count) { instruction };
        parts.Add(
            """
            **Explore reports to incorporate**

            Incorporate these Explore findings into Recap / Agent actions only; do not start implementation this turn.
            """);

        foreach (var block in reportBlocks)
        {
            if (string.IsNullOrWhiteSpace(block))
                continue;
            parts.Add(block.Trim());
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Creates a BeginBuildPlan turn (LLM runs via <c>PromptBeginBuildPlanAsync</c>).
    /// Does not append to history — the prompt entry point calls <see cref="DysonAgentSession"/> AddTurn.
    /// </summary>
    public static DysonAgentTurn CreateTurn(
        string planRelativePath,
        IReadOnlyList<string>? reportBlocks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planRelativePath);
        var path = planRelativePath.Trim().Replace('\\', '/');

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.BeginBuildPlan,
            Instruction = BuildInstruction(path, reportBlocks),
            PlanRelativePath = path,
            StartedUtc = DateTime.UtcNow,
        };
    }
}

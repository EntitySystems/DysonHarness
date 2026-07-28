namespace DysonHarness;

/// <summary>
/// Factories and instruction text for BeginBuildPlan harness turns (composer Build plan).
/// Layout-only reply (Recap + multi-drone Agent actions prep) plus required ReadFile /
/// CreateTodo tools; prefers parallel Drone multitasking on the continuation turn;
/// no implementation or StartSubagent this turn.
/// </summary>
public static class DysonBeginBuildPlanFlow
{
    /// <summary>
    /// Normal-turn prompt the host enqueues after a successful BeginBuildPlan so Work
    /// continues without a manual user message. Prefers parallel Drone multitasking.
    /// </summary>
    public const string ContinuationPrompt =
        "Continue the plan implementation as per previous instructions. "
        + "Prefer parallel Drone multitasking (`StartSubagent`) for independent Agent actions workstreams; "
        + "Wait only for hard prerequisites. "
        + "Session todos already exist for the Agent actions checklist; add or update todos as work unfolds.";

    /// <summary>
    /// Whether the host should enqueue <see cref="ContinuationPrompt"/> after this turn
    /// completes successfully. True only for <see cref="DysonAgentTurnKind.BeginBuildPlan"/>.
    /// </summary>
    public static bool ShouldEnqueueBuildContinuation(DysonAgentTurnKind kind) =>
        kind == DysonAgentTurnKind.BeginBuildPlan;

    /// <summary>
    /// Harness mandate: layout-only turn — read the plan, emit Recap + Agent actions
    /// (technical multi-drone prep; multitasking preferred), and create session todos
    /// for each Agent actions item. No implementation / StartSubagent this turn; the
    /// host enqueues a Normal continuation next.
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
            - **`## Agent actions`** — Technical high-level prep for implementation with multiple Drones: split independent workstreams; name each Drone brief (scope, files/areas, success criteria, handoff); call out shared prerequisites and merge/integration order. Prefer parallel multitasking over serial solo work; serialize only when a hard dependency requires it.

            **Multitasking is superior** — plan to `StartSubagent` several Drones in parallel on the next turn when workstreams are independent; do not default to doing everything yourself sequentially.

            Required tools this turn: `ReadFile` on the plan path, then `CreateTodo` for each Agent actions item (`displayName` + unique `taskCode`).
            More todos may be added later during implementation (`CreateTodo` / `UpdateTodo` on later turns).
            Do not call other tools this turn: no `StartSubagent` / Drones, no `WriteFile`, no shell, no product work.
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

using System.Text.Json;
using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>
/// Scripted repository-extraction showcase. Seeds real catalog tool names onto a live
/// <see cref="DysonAgentTurn"/> so the UI tool rows, todos, subagent cards, and
/// <see cref="DysonAgentTurnKind.SubagentReportProcessing"/> handoffs update for real.
/// </summary>
public static class DysonVisualDemoScenario
{
    public const string SessionTitle = "DEMO: Repository extraction";
    public const string WorkDirectoryName = "DysonHarness (DEMO)";
    public const string DemoSlug = "demo-mock";

    public const string UserPrompt =
        "Plan how we move all EF Core DbContext calls out of UI/host code into repositories. "
        + "Explore the current data-access surface first, then hand implementation to a Drone.";

    public const string ExploreTask =
        "Find every EF Core DbContext / IDyson*Repository call site in UI and host code. "
        + "Report concrete files and a recommended repository split.";

    public const string DroneTask =
        "Extract session + work-directory persistence behind IDysonSessionRepository and "
        + "IDysonWorkDirectoryRepository. Do not change engine contracts.";

    public static IReadOnlyList<DysonToolCall> SeedTools(DysonAgentSession session, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        if (session.Parent is not null
            || string.Equals(session.Mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase)
            || string.Equals(session.Mode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
        {
            return SeedChild(session);
        }

        if (turn.Kind == DysonAgentTurnKind.SubagentReportProcessing)
            return HasDrone(session) ? SeedRootAfterDrone() : SeedRootAfterExplore();

        if (!HasUserKickoff(session))
            return SeedRootKickoff();

        return [];
    }

    public static string ComposeThought(DysonAgentSession session, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        if (session.Parent is not null
            && string.Equals(session.Mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
        {
            return "# Map DbContext call sites\n\nSearch UI/host first, then read the repository contracts.";
        }

        if (session.Parent is not null
            && string.Equals(session.Mode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
        {
            return "# Extract repositories\n\nKeep engine types; move host-owned queries behind existing interfaces.";
        }

        if (turn.Kind == DysonAgentTurnKind.SubagentReportProcessing)
        {
            return HasDrone(session)
                ? "# Integrate Drone report\n\nMark remaining todos complete and hand off to the user."
                : "# Read Explore report\n\nDispatch a Drone for the persistence extraction slice.";
        }

        return "# Repository extraction\n\nExplore call sites, file todos, then spawn Explore — do not implement yet.";
    }

    public static string ComposeReply(DysonAgentSession session, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        if (session.Parent is not null
            && string.Equals(session.Mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Explore: DbContext call sites

                ## Findings
                UI/host still constructs `DysonDbContext` / repo facades instead of going through
                `IDysonSessionRepository` and `IDysonWorkDirectoryRepository`.

                | Area | Files | Issue |
                | --- | --- | --- |
                | Session persist | `Harness.UI/Demo/DysonUiHost.cs` | Host calls store helpers that wrap EF |
                | Workdirs | `Harness.LocalDb/Storage/DysonWorkDirectoryRepository.cs` | Correct repo — UI should stop bypassing it |
                | Models | `Harness.LocalDb/Storage/DysonModelRepository.cs` | Already behind `IDysonModelRepository` |

                ## Recommended split
                1. Keep engine on the `IDyson*Repository` interfaces.
                2. Move remaining host EF usage into LocalDb repos.
                3. UI binds repositories only.

                Report submitted to parent (turn handoff).
                """;
        }

        if (session.Parent is not null
            && string.Equals(session.Mode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Drone: persistence extraction

                ## Agent actions
                1. Confirmed call sites against Explore report.
                2. Routed session + workdir writes through existing repository interfaces.
                3. Left engine contracts unchanged.

                ## Recap
                Host no longer talks to `DysonDbContext` directly for session/workdir rows.
                Remaining model CRUD already used `IDysonModelRepository`.

                Report submitted to parent (turn handoff).
                """;
        }

        if (turn.Kind == DysonAgentTurnKind.SubagentReportProcessing)
        {
            return HasDrone(session)
                ? """
                    # Subagent report: repositories landed

                    ## Report
                    Drone finished the persistence extraction. Engine interfaces are unchanged.

                    ## Next
                    Todos are complete. Ready for a review pass on `DysonUiHost` persist helpers.
                    """
                : """
                    # Subagent report: Explore complete

                    ## Report
                    Explore mapped the DbContext / repository split. Dispatching a Drone for the
                    session + work-directory extraction slice.

                    ## Next
                    Wait for the Drone handoff before claiming the work done.
                    """;
        }

        return """
            # Plan: move DB calls behind repositories

            ## Recap
            This is a turn-based Work session (not chat). I filed todos, grepped the host/UI
            persist surface, and started an Explore subagent.

            ## Agent actions
            1. Explore reports call sites + recommended repository split.
            2. A later Drone implements against `IDysonSessionRepository` / `IDysonWorkDirectoryRepository`.
            3. Parent consumes each handoff as a `SubagentReportProcessing` turn.

            Explore is running in the background — its report will arrive as a harness turn.
            """;
    }

    public static string MockToolContent(DysonToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var name = call.ToolName;

        if (string.Equals(name, "Grep", StringComparison.OrdinalIgnoreCase))
        {
            return """
                src/Harness/Harness.UI/Demo/DysonUiHost.cs:2445:        var createProvider = await _models.CreateProviderAsync(
                src/Harness/Harness.UI/Demo/DysonUiHost.cs:2594:        var workDir = await _workDirectories.GetAsync(workDirectoryId.Value, cancellationToken)
                src/Harness/Harness.LocalDb/Storage/DysonSessionRepository.cs:18:        await using var db = await _accessor.CreateDbContextAsync(cancellationToken)
                src/Harness/Harness.LocalDb/Storage/DysonWorkDirectoryRepository.cs:22:        await using var db = await _accessor.CreateDbContextAsync(cancellationToken)
                """;
        }

        if (string.Equals(name, "ReadFile", StringComparison.OrdinalIgnoreCase))
        {
            return """
                1|public sealed class DysonSessionRepository : IDysonSessionRepository
                2|{
                3|    private readonly DysonDbAccessor _accessor;
                4|
                5|    public async Task<Result<Guid, string>> CreateSessionAsync(...)
                6|    {
                7|        await using var db = await _accessor.CreateDbContextAsync(cancellationToken);
                8|        db.Sessions.Add(row);
                9|        await db.SaveChangesAsync(cancellationToken);
                10|    }
                """;
        }

        if (string.Equals(name, "ListDirectory", StringComparison.OrdinalIgnoreCase))
        {
            return """
                Harness.Abstractions/Storage/IDysonSessionRepository.cs
                Harness.Abstractions/Storage/IDysonWorkDirectoryRepository.cs
                Harness.LocalDb/Storage/DysonSessionRepository.cs
                Harness.LocalDb/Storage/DysonWorkDirectoryRepository.cs
                Harness.UI/Demo/DysonUiHost.cs
                """;
        }

        if (string.Equals(name, "WriteFile", StringComparison.OrdinalIgnoreCase))
        {
            return """{"ok":true,"path":"src/Harness/Harness.LocalDb/Storage/DysonSessionRepository.cs","edits":1}""";
        }

        return $"[demo] {name} ok — args={Truncate(call.ArgumentsJson, 80)}";
    }

    public static bool HasDrone(DysonAgentSession session) =>
        session.SubSessions.Any(child =>
            string.Equals(child.Mode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase));

    public static bool HasUserKickoff(DysonAgentSession session) =>
        session.Turns.Any(t =>
            t.Kind is DysonAgentTurnKind.Normal or DysonAgentTurnKind.InitializeSession);

    private static IReadOnlyList<DysonToolCall> SeedRootKickoff() =>
    [
        Call("RenameSession", 0, Json(new { title = SessionTitle })),
        Call("Grep", 0, Json(new { pattern = "DysonDbContext|CreateDbContextAsync", path = "src/Harness", glob = "*.cs" })),
        Call("ReadFile", 0, Json(new { path = "src/Harness/Harness.LocalDb/Storage/DysonSessionRepository.cs", limit = 40 })),
        Call("ListDirectory", 0, Json(new { path = "src/Harness/Harness.LocalDb/Storage" })),
        Call("CreateTodo", 1, Json(new { taskCode = "map-call-sites", displayName = "Map DbContext call sites", status = "ongoing" })),
        Call("CreateTodo", 1, Json(new { taskCode = "extract-session-repo", displayName = "Extract session persist into repository", status = "pending" })),
        Call("CreateTodo", 1, Json(new { taskCode = "extract-workdir-repo", displayName = "Extract work-directory persist into repository", status = "pending" })),
        Call("StartSubagent", 2, Json(new { agentMode = DysonAgentModes.Explore, task = ExploreTask })),
    ];

    private static IReadOnlyList<DysonToolCall> SeedRootAfterExplore() =>
    [
        Call("UpdateTodo", 0, Json(new { taskCode = "map-call-sites", status = "complete", appendComment = "Explore report received." })),
        Call("UpdateTodo", 0, Json(new { taskCode = "extract-session-repo", status = "ongoing" })),
        Call("StartSubagent", 1, Json(new { agentMode = DysonAgentModes.Drone, task = DroneTask })),
    ];

    private static IReadOnlyList<DysonToolCall> SeedRootAfterDrone() =>
    [
        Call("UpdateTodo", 0, Json(new { taskCode = "extract-session-repo", status = "complete", appendComment = "Drone report received." })),
        Call("UpdateTodo", 0, Json(new { taskCode = "extract-workdir-repo", status = "complete" })),
        Call("ListTodos", 1, "{}"),
    ];

    private static IReadOnlyList<DysonToolCall> SeedChild(DysonAgentSession session)
    {
        if (string.Equals(session.Mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Call("Grep", 0, Json(new { pattern = "DysonDbContext|IDysonSessionRepository", path = "src/Harness", glob = "*.cs" })),
                Call("ReadFile", 0, Json(new { path = "src/Harness/Harness.Abstractions/Storage/IDysonSessionRepository.cs" })),
                Call("ListDirectory", 1, Json(new { path = "src/Harness/Harness.UI/Demo" })),
                Call("SubmitSubagentReport", 2, Json(new
                {
                    status = "completed",
                    summary =
                        "UI/host still reaches EF around session + workdir persist. "
                        + "Keep engine on IDysonSessionRepository / IDysonWorkDirectoryRepository; "
                        + "move remaining host EF into LocalDb repos.",
                })),
            ];
        }

        return
        [
            Call("ReadFile", 0, Json(new { path = "src/Harness/Harness.UI/Demo/DysonUiHost.cs", offset = 2430, limit = 40 })),
            Call("WriteFile", 1, """{"path":"src/Harness/Harness.LocalDb/Storage/DysonSessionRepository.cs","old_text":"host-owned EF","new_text":"repository method"}"""),
            Call("ListTodos", 1, "{}"),
            Call("SubmitSubagentReport", 2, Json(new
            {
                status = "completed",
                summary =
                    "Session and work-directory writes now go through IDysonSessionRepository "
                    + "and IDysonWorkDirectoryRepository. Engine contracts unchanged.",
            })),
        ];
    }

    private static DysonToolCall Call(string toolName, int stage, string argumentsJson) =>
        new()
        {
            CallId = "",
            ToolName = toolName,
            Stage = stage,
            ArgumentsJson = argumentsJson,
        };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value ?? "";
        return value[..max] + "…";
    }
}

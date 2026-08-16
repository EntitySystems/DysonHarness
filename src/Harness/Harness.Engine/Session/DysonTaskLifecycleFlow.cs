namespace DysonHarness;

/// <summary>
/// Normalized Automatic code review setting. <see cref="High"/> is persisted/display-only
/// and must not start a review.
/// </summary>
public enum DysonAutomaticCodeReviewLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    /// <summary>Unsupported. Display as disabled; treat as non-runnable.</summary>
    High = 3,
}

/// <summary>Follow-up behavior for an automatic Bug Review orchestration turn.</summary>
public enum DysonAutomaticCodeReviewAction
{
    ReportOnly = 0,
    AutomaticallyFix = 1,
}

/// <summary>Stable-boundary action the host should take after <see cref="DysonTaskLifecycleFlow.Evaluate"/>.</summary>
public enum DysonTaskLifecycleKind
{
    /// <summary>Enqueue a <see cref="DysonAgentTurnKind.TaskEndReflect"/> harness turn.</summary>
    TaskEndReflectionRequired = 0,
    /// <summary>
    /// All todos complete and ReportSummary finished. Host reads Automatic code review:
    /// none/high → persist terminal; low/medium → enqueue BugReview.
    /// </summary>
    CodeReviewReady = 1,
    /// <summary>BugReview orchestration turn completed and its reviewer is terminal; persist root completed.</summary>
    ReadyToFinalize = 2,
}

/// <summary>Raised by <see cref="DysonAgentSession.EvaluateTaskLifecycle"/> when a lifecycle action is required.</summary>
public sealed class DysonTaskLifecycleEventArgs : EventArgs
{
    public required DysonTaskLifecycleKind Kind { get; init; }
}

/// <summary>Result of <see cref="DysonTaskLifecycleFlow.Evaluate"/>.</summary>
/// <param name="Kind">Action to take, or null when the session is not at a stable lifecycle boundary.</param>
public readonly record struct DysonTaskLifecycleDecision(DysonTaskLifecycleKind? Kind)
{
    public bool HasAction => Kind is not null;
}

/// <summary>
/// Root-session task-lifecycle evaluator: review-level normalization, predicates, and
/// TaskEndReflect / BugReview turn factories. Host owns settings, queueing, and terminal persist.
/// </summary>
public static class DysonTaskLifecycleFlow
{
    public const string ReviewLevelNone = "none";
    public const string ReviewLevelLow = "low";
    public const string ReviewLevelMedium = "medium";
    public const string ReviewLevelHigh = "high";
    public const string ReviewActionReportOnly = "report_only";
    public const string ReviewActionAutomaticallyFix = "automatically_fix";

    public const string TaskEndReflectInstruction = """
        The prior turn completed. No subagents are running. One or more todos are still pending or ongoing.

        Review the pending work, perform and verify what remains, and update todo status accurately rather than declaring success prematurely.
        Do not call CompleteTask while required work is unfinished.

        Check each incomplete todo against evidence in this session (files, tests, tool results). Mark complete only what is actually done; keep pending or ongoing items honest.
        """;

    public const string BugReviewSharedInstruction = """
        The root task has passed completion confirmation and the report-summary boundary.

        Start exactly one Bug Review subagent now: call StartSubagent with agentMode `Bug Review`. Do not pass an explicit modelSlug (omit StartSubagent.modelSlug so the configured Bug Review default or inherited parent model is used). Wait for that subagent in this turn (WaitForSubagent). After it returns, write a concise, evidence-based bug report.

        The reviewer remains review-only. Require each finding to include severity/impact, path, evidence or reproduction steps, and a fix direction. Avoid style-only issues. If there are no findings, say so explicitly.

        Do not start a second reviewer. Do not mark the task complete yourself; the harness finalizes after this turn.
        """;

    public const string BugReviewLowInstruction = """
        Automatic code review is enabled at Low. Direct the reviewer's effort to modified files and their behavior. Find bugs and make a proper report after the reviewer returns.
        """;

    public const string BugReviewMediumInstruction = """
        Automatic code review is enabled at Medium. Direct a thorough review at modified files plus their related APIs, persistence, async behavior, external interactions, error paths, and regression risks. Wait for the reviewer and make a proper report.
        """;

    public const string BugReviewReportOnlyInstruction = """
        Action: report only. Do not modify files. Distinguish confirmed bugs from unconfirmed risks, and explicitly report when no bugs were found.
        """;

    public const string BugReviewAutomaticallyFixInstruction = """
        Action: automatically fix. The reviewer remains review-only. After it returns, validate each claimed bug; fix only confirmed bugs, run appropriate verification, and report unresolved findings. Do not start another review or enter an unlimited fix/review loop.
        """;

    /// <summary>True when the setting should spawn a Bug Review orchestration turn.</summary>
    public static bool IsReviewRunnable(DysonAutomaticCodeReviewLevel level) =>
        level is DysonAutomaticCodeReviewLevel.Low or DysonAutomaticCodeReviewLevel.Medium;

    /// <summary>
    /// Parses Automatic code review settings text.
    /// Unknown / empty → <see cref="DysonAutomaticCodeReviewLevel.None"/> (do not start a review).
    /// </summary>
    public static DysonAutomaticCodeReviewLevel NormalizeReviewLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DysonAutomaticCodeReviewLevel.None;

        return value.Trim().ToLowerInvariant() switch
        {
            ReviewLevelNone => DysonAutomaticCodeReviewLevel.None,
            ReviewLevelLow => DysonAutomaticCodeReviewLevel.Low,
            ReviewLevelMedium => DysonAutomaticCodeReviewLevel.Medium,
            ReviewLevelHigh => DysonAutomaticCodeReviewLevel.High,
            _ => DysonAutomaticCodeReviewLevel.None,
        };
    }

    /// <summary>Parses the persisted automatic-review action; unknown/missing means report only.</summary>
    public static DysonAutomaticCodeReviewAction NormalizeReviewAction(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            ReviewActionAutomaticallyFix => DysonAutomaticCodeReviewAction.AutomaticallyFix,
            _ => DysonAutomaticCodeReviewAction.ReportOnly,
        };

    /// <summary>
    /// Legacy compatibility: <c>EndOfTaskAutoReview</c> false/missing → none;
    /// true plus <c>SelfReviewIntensity</c> → low or medium (anything else, including high → medium).
    /// </summary>
    public static DysonAutomaticCodeReviewLevel NormalizeFromLegacy(
        string? endOfTaskAutoReview,
        string? selfReviewIntensity)
    {
        if (!IsLegacyAutoReviewEnabled(endOfTaskAutoReview))
            return DysonAutomaticCodeReviewLevel.None;

        if (string.Equals(selfReviewIntensity?.Trim(), ReviewLevelLow, StringComparison.OrdinalIgnoreCase))
            return DysonAutomaticCodeReviewLevel.Low;

        return DysonAutomaticCodeReviewLevel.Medium;
    }

    /// <summary>Persisted string for a normalized level (<c>none</c>/<c>low</c>/<c>medium</c>/<c>high</c>).</summary>
    public static string ToPersistedValue(DysonAutomaticCodeReviewLevel level) =>
        level switch
        {
            DysonAutomaticCodeReviewLevel.Low => ReviewLevelLow,
            DysonAutomaticCodeReviewLevel.Medium => ReviewLevelMedium,
            DysonAutomaticCodeReviewLevel.High => ReviewLevelHigh,
            _ => ReviewLevelNone,
        };

    /// <summary>
    /// Creates a <see cref="DysonAgentTurnKind.TaskEndReflect"/> turn.
    /// Does not append to session history.
    /// </summary>
    public static DysonAgentTurn CreateTaskEndReflectTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.TaskEndReflect,
            Instruction = TaskEndReflectInstruction,
            StartedUtc = DateTime.UtcNow,
        };

    /// <summary>Alias of <see cref="IsReviewRunnable"/> for host/settings predicates.</summary>
    public static bool ShouldStartReview(DysonAutomaticCodeReviewLevel level) =>
        IsReviewRunnable(level);

    /// <summary>
    /// Creates a <see cref="DysonAgentTurnKind.BugReview"/> turn for a runnable level.
    /// Does not append to session history.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> is not <see cref="DysonAutomaticCodeReviewLevel.Low"/> or
    /// <see cref="DysonAutomaticCodeReviewLevel.Medium"/> (programmer error — check
    /// <see cref="IsReviewRunnable"/> first).
    /// </exception>
    public static DysonAgentTurn CreateBugReviewTurn(DysonAutomaticCodeReviewLevel level) =>
        CreateBugReviewTurn(level, DysonAutomaticCodeReviewAction.ReportOnly, worktreeScope: null);

    /// <summary>
    /// Creates a BugReview turn with its configured action and an optional git-status scope.
    /// </summary>
    public static DysonAgentTurn CreateBugReviewTurn(
        DysonAutomaticCodeReviewLevel level,
        DysonAutomaticCodeReviewAction action,
        string? worktreeScope)
    {
        if (!IsReviewRunnable(level))
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "CreateBugReviewTurn requires Low or Medium.");
        }

        var levelInstruction = level == DysonAutomaticCodeReviewLevel.Low
            ? BugReviewLowInstruction
            : BugReviewMediumInstruction;
        var actionInstruction = action == DysonAutomaticCodeReviewAction.AutomaticallyFix
            ? BugReviewAutomaticallyFixInstruction
            : BugReviewReportOnlyInstruction;
        var scopeInstruction = string.IsNullOrWhiteSpace(worktreeScope)
            ? "Worktree scope was not available; determine the relevant changes before reviewing."
            : $"## Worktree scope at review start\n{worktreeScope.Trim()}";

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.BugReview,
            Instruction = $"{BugReviewSharedInstruction}\n\n{levelInstruction}\n\n{actionInstruction}\n\n{scopeInstruction}",
            StartedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Stable-boundary evaluation. No action unless: root, non-empty todos, no pending follow-up,
    /// no in-flight prompt, no active descendant, all turns finalized, session still Active.
    /// Dedupes reflection / review from durable turn history (no extra DB column).
    /// </summary>
    public static DysonTaskLifecycleDecision Evaluate(DysonAgentSession session, bool hasActiveDescendant)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Parent is not null
            || session.IsTerminal
            || session.Status != DysonSessionStatus.Active
            || hasActiveDescendant
            || session.InFlightPromptTurn is not null
            || session.HasPendingTurn)
        {
            return default;
        }

        var todos = session.Todos;
        if (todos.Count == 0)
            return default;

        var turns = session.Turns;
        if (turns.Count == 0)
            return default;

        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].CompletedUtc is null)
                return default;
        }

        var last = turns[^1];
        var hasIncompleteTodo = false;
        for (var i = 0; i < todos.Count; i++)
        {
            if (todos[i].Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing)
            {
                hasIncompleteTodo = true;
                break;
            }
        }

        var hasBugReview = false;
        var bugReviewCompleted = false;
        for (var i = 0; i < turns.Count; i++)
        {
            var kind = turns[i].Kind;
            if (kind != DysonAgentTurnKind.BugReview)
                continue;

            hasBugReview = true;
            if (turns[i].CompletedUtc is not null)
                bugReviewCompleted = true;
        }

        if (hasBugReview)
        {
            // A report-only review normally leaves todos unchanged, but automatically-fix may
            // honestly add a follow-up. Never terminalize the root while that work remains.
            if (hasIncompleteTodo)
                return default;

            if (bugReviewCompleted)
                return new DysonTaskLifecycleDecision(DysonTaskLifecycleKind.ReadyToFinalize);

            return default;
        }

        if (hasIncompleteTodo)
        {
            // The reflection turn itself is not a trigger, but a later substantive turn may
            // need another reflection if pending work remains.
            if (!IsTaskEndReflectionTriggerKind(last.Kind))
                return default;

            return new DysonTaskLifecycleDecision(DysonTaskLifecycleKind.TaskEndReflectionRequired);
        }

        if (last.Kind == DysonAgentTurnKind.ReportSummary)
            return new DysonTaskLifecycleDecision(DysonTaskLifecycleKind.CodeReviewReady);

        return default;
    }

    /// <summary>
    /// Substantive (or completion-boundary) kinds that may trigger one TaskEndReflect
    /// when todos are still pending/ongoing. Pure lifecycle/finalization kinds are excluded
    /// so a no-op reflection cannot spin.
    /// </summary>
    public static bool IsTaskEndReflectionTriggerKind(DysonAgentTurnKind kind) =>
        kind is DysonAgentTurnKind.Normal
            or DysonAgentTurnKind.Continuation
            or DysonAgentTurnKind.BeginBuildPlan
            or DysonAgentTurnKind.SubagentReportProcessing
            or DysonAgentTurnKind.ShellExited
            or DysonAgentTurnKind.ReportSummary;

    private static bool IsLegacyAutoReviewEnabled(string? endOfTaskAutoReview)
    {
        if (string.IsNullOrWhiteSpace(endOfTaskAutoReview))
            return false;

        return bool.TryParse(endOfTaskAutoReview.Trim(), out var parsed) && parsed;
    }
}

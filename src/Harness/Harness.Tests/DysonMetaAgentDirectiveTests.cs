using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Distinctive-substring checks for Meta Agent / Meta Agent Drone system prompts.
/// Whole-text equality is avoided so wording tweaks do not force a test edit.
/// </summary>
public class DysonMetaAgentDirectiveTests
{
    [Fact]
    public void Run()
    {
        AssertMetaAgentForMode();
        AssertMetaAgentDroneForMode();
        AssertBothDifferFromWork();
        AssertSubmitMetaPlanIsNotAReportInMandate();
        AssertMetaAgentDroneFirstTurnMandate();
    }

    private static void AssertMetaAgentForMode()
    {
        var text = Prompt("Meta Agent");
        MustContain(text, DysonAgentSystemPrompts.SharedPreamble.Trim(), "Meta Agent ForMode shared preamble");
        MustContain(text, "never block", "Meta Agent ForMode");
        MustContain(text, "BrowserWaitForNavigation", "Meta Agent ForMode");
        MustContain(text, "Reuse over re-spawn", "Meta Agent ForMode");
        MustContain(text, "You cannot touch the filesystem", "Meta Agent ForMode");
        // The meta page renders posted messages only; prose-only turns show the user nothing.
        // Pinned so the one instruction standing between the agent and a blank screen is not softened away.
        MustContain(text, "PostConversationMessage is your only voice", "Meta Agent ForMode");
        // The todo list outlives the roster and the transcript; posted messages do not come back on later turns.
        MustContain(text, "ListTodos before you answer whether work was dispatched", "Meta Agent ForMode");
        MustContain(text, "A posted message is shown to the user and is not in later turns", "Meta Agent ForMode");
        MustContain(text, "blocked inside TriggerParentEvent", "Meta Agent ForMode");
        MustContain(text, "only a status", "Meta Agent ForMode");
        MustContain(text, "Remember the subagentId and eventId", "Meta Agent ForMode");
        MustContain(text, "Do not mention the continuation", "Meta Agent ForMode");
        MustNotContain(text, "reports failed with the question", "Meta Agent ForMode");
    }

    private static void AssertMetaAgentDroneForMode()
    {
        var text = Prompt("Meta Agent Drone");
        MustContain(text, "Explore first", "Meta Agent Drone ForMode");
        MustContain(text, "SubmitMetaPlan", "Meta Agent Drone ForMode");
        MustContain(text, "must still SubmitSubagentReport", "Meta Agent Drone ForMode");
        MustContain(text, "Do not implement it, and do not commit anything on your branch.", "Meta Agent Drone ForMode");
        MustContain(text, "TriggerParentEvent is how you talk to the Meta Agent", "Meta Agent Drone ForMode");
        MustContain(text, "when you finish a section of the implementation", "Meta Agent Drone ForMode");
        MustContain(text, "Do not SubmitSubagentReport to ask a question or to give a status", "Meta Agent Drone ForMode");
        MustNotContain(text, "There is no path from you to the user", "Meta Agent Drone ForMode");
    }

    private static void AssertBothDifferFromWork()
    {
        var meta = Prompt("Meta Agent");
        var drone = Prompt("Meta Agent Drone");
        var work = Prompt("Work");
        if (meta == work)
            throw new InvalidOperationException("ForMode(\"Meta Agent\") must differ from ForMode(\"Work\").");
        if (drone == work)
            throw new InvalidOperationException("ForMode(\"Meta Agent Drone\") must differ from ForMode(\"Work\").");
        if (meta == drone)
            throw new InvalidOperationException("ForMode(\"Meta Agent\") must differ from ForMode(\"Meta Agent Drone\").");
    }

    private static void AssertSubmitMetaPlanIsNotAReportInMandate()
    {
        MustContain(
            DysonAgentSystemPrompts.SubagentReportRequiredMandate,
            "SubmitMetaPlan is not a report",
            nameof(DysonAgentSystemPrompts.SubagentReportRequiredMandate));
        MustContain(
            DysonAgentSystemPrompts.SubagentReportRequiredMandate,
            "must still call SubmitSubagentReport naming the planId",
            nameof(DysonAgentSystemPrompts.SubagentReportRequiredMandate));
    }

    private static void AssertMetaAgentDroneFirstTurnMandate()
    {
        var text = DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate;
        MustContain(text, "That is the final state only", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
        MustContain(text, "At each section boundary", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
        MustContain(text, "Never report failed just to ask a question or to give a status", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
        MustNotContain(text, "Blocked or needing a decision", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
    }

    private static string Prompt(string mode)
    {
        var result = DysonAgentSystemPrompts.ForMode(mode);
        if (result.IsError)
            throw new InvalidOperationException($"ForMode(\"{mode}\") failed: {result.Error}");
        if (string.IsNullOrWhiteSpace(result.Value))
            throw new InvalidOperationException($"ForMode(\"{mode}\") returned empty.");
        return result.Value;
    }

    private static void MustContain(string text, string needle, string subject)
    {
        if (!text.Contains(needle, StringComparison.Ordinal))
            throw new InvalidOperationException($"{subject} must contain '{needle}'.");
    }

    private static void MustNotContain(string text, string needle, string subject)
    {
        if (text.Contains(needle, StringComparison.Ordinal))
            throw new InvalidOperationException($"{subject} must not contain '{needle}'.");
    }
}

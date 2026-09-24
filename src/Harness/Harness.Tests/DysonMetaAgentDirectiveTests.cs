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
        MustContain(text, "ListNotes", "Meta Agent ForMode");
        MustContain(text, "CanCreateNote", "Meta Agent ForMode");
        MustContain(text, "standing user guidelines", "Meta Agent ForMode");
        MustNotContain(text, "The only disk access", "Meta Agent ForMode");
        MustNotContain(text, ".dyson/scratch", "Meta Agent ForMode");
        // The meta page renders posted messages only; prose-only turns show the user nothing.
        // Pinned so the one instruction standing between the agent and a blank screen is not softened away.
        MustContain(text, "PostConversationMessage is your only voice", "Meta Agent ForMode");
        MustContain(
            text,
            "Conversation actions are required whenever a posted message names something the user would open. When you PostConversationMessage and the message names a plan, a workspace file, or an http(s) URL the user would reasonably want to open, attach one action per target. Do this on status updates, questions, and results — not only when a plan is first submitted. A message may carry several actions, up to 8 (the tool maximum; do not ask for more). name is a short human label; func is one of these built-in keys and needs no RegisterConversationAction call: open_plan:{planId} opens that plan, open_file:{path} opens a work-relative file, and open_url:{url} opens an http or https link. Do not attach actions for data that is not a plan id, a workspace file, or an http(s) URL. Do not invent func keys.",
            "Meta Agent ForMode");
        MustContain(text, "visualizationId", "Meta Agent ForMode");
        MustContain(text, "WriteTempFile", "Meta Agent ForMode");
        MustContain(text, "ReadTempFile", "Meta Agent ForMode");
        // The todo list outlives the roster and the transcript; posted messages do not come back on later turns.
        MustContain(text, "ListTodos before you answer whether work was dispatched", "Meta Agent ForMode");
        MustContain(text, "A posted message is shown to the user and is not in later turns", "Meta Agent ForMode");
        MustContain(text, "blocked inside TriggerParentEvent", "Meta Agent ForMode");
        MustContain(
            text,
            "a parent-event continuation is mandatory. Before that turn ends, call RespondToSubagentEvent unless this is a question only the user can answer. Status (what landed, what is next): ack the same turn and PostConversationMessage that status. A question you already know: answer the same turn; do not ask the user. A question only the user can decide: PostConversationMessage the question, do not respond yet, keep subagentId and eventId; the next user message will carry the same event; then RespondToSubagentEvent with their answer. Do not start another drone for the same question. Do not end a status turn without the ack.",
            "Meta Agent ForMode");
        MustContain(text, "Do not mention the continuation", "Meta Agent ForMode");
        MustNotContain(text, "reports failed with the question", "Meta Agent ForMode");
        MustContain(text, "useWorktree false", "Meta Agent ForMode");
        MustContain(text, "existingWorktreePath", "Meta Agent ForMode");
        MustContain(text, "StartAsyncMetaAgentDrone", "Meta Agent ForMode");
        MustContain(text, "StartAsyncBugReviewAgent", "Meta Agent ForMode");
        MustContain(text, "StartAsyncSecurityReviewAgent", "Meta Agent ForMode");
        MustContain(text, "Do not edit files yourself", "Meta Agent ForMode");
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
        MustContain(
            text,
            "you are the parent of events from your own children. RespondToSubagentEvent before the turn ends. Status: short ack the same turn. A question you know: answer the same turn. A question you do not know: TriggerParentEvent to your parent with kind message, wait for that reply, then RespondToSubagentEvent to the child with the answer. You cannot PostConversationMessage. Do not spawn another agent for the same question. Do not use kind askQuestion or promptUserDialog.",
            "Meta Agent Drone ForMode");
        MustContain(text, "StartAsyncBugReviewAgent", "Meta Agent Drone ForMode");
        MustContain(text, "StartAsyncSecurityReviewAgent", "Meta Agent Drone ForMode");
        MustContain(text, "StartAsyncMetaAgentDrone", "Meta Agent Drone ForMode");
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
        MustContain(text, "StartAsyncBugReviewAgent", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
        MustContain(text, "StartAsyncSecurityReviewAgent", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
        MustContain(text, "contextFiles", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
        MustNotContain(text, "Blocked or needing a decision", nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
        MustContain(
            text,
            "you are the parent of events from your own children. RespondToSubagentEvent before the turn ends. Status: short ack the same turn. A question you know: answer the same turn. A question you do not know: TriggerParentEvent to your parent with kind message, wait for that reply, then RespondToSubagentEvent to the child with the answer. You cannot PostConversationMessage. Do not spawn another agent for the same question. Do not use kind askQuestion or promptUserDialog.",
            nameof(DysonAgentSystemPrompts.MetaAgentDroneFirstTurnMandate));
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

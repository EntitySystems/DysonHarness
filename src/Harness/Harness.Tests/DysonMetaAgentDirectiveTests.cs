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
    }

    private static void AssertMetaAgentForMode()
    {
        var text = Prompt("Meta Agent");
        MustContain(text, DysonAgentSystemPrompts.SharedPreamble.Trim(), "Meta Agent ForMode shared preamble");
        MustContain(text, "never block", "Meta Agent ForMode");
        MustContain(text, "Reuse over re-spawn", "Meta Agent ForMode");
        MustContain(text, "You cannot touch the filesystem", "Meta Agent ForMode");
    }

    private static void AssertMetaAgentDroneForMode()
    {
        var text = Prompt("Meta Agent Drone");
        MustContain(text, "Explore first", "Meta Agent Drone ForMode");
        MustContain(text, "SubmitMetaPlan", "Meta Agent Drone ForMode");
        MustContain(text, "must still SubmitSubagentReport", "Meta Agent Drone ForMode");
        MustContain(text, "Do not implement it, and do not commit anything on your branch.", "Meta Agent Drone ForMode");
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
}

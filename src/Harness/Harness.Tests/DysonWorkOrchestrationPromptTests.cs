using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Work/Drone Explore-always-wait policy in system-prompt constants
/// (assert-only; Plan first-turn tests stay in DysonPlanFirstTurnMandateTests).
/// </summary>
public class DysonWorkOrchestrationPromptTests
{
    [Fact]
    public void Run()
    {
        AssertWorkDirectiveExploreAlwaysWait();
        AssertDroneDirectiveExploreAlwaysWait();
        AssertDroneFirstTurnContextMandateExploreAlwaysWait();
    }

    private static void AssertWorkDirectiveExploreAlwaysWait()
    {
        var text = DysonAgentSystemPrompts.WorkDirective;
        MustContain(
            text,
            "If you started an Explore, that Explore is always a blocker",
            nameof(DysonAgentSystemPrompts.WorkDirective));
        MustContain(
            text,
            "If you StartSubagent an Explore, no further parent work may occur until that Explore’s result has been returned",
            nameof(DysonAgentSystemPrompts.WorkDirective));
        MustContain(
            text,
            "call WaitForSubagent on a later stage of the same turn",
            nameof(DysonAgentSystemPrompts.WorkDirective));
        MustNotContain(
            text,
            "Otherwise do not Wait — keep multitasking",
            nameof(DysonAgentSystemPrompts.WorkDirective));
    }

    private static void AssertDroneDirectiveExploreAlwaysWait()
    {
        var text = DysonAgentSystemPrompts.DroneDirective;
        MustContain(
            text,
            "If you start an Explore, WaitForSubagent on a later stage of the same turn and do no further Drone work until the report returns",
            nameof(DysonAgentSystemPrompts.DroneDirective));
        MustContain(
            text,
            "an Explore you start is always a blocker — WaitForSubagent until it finishes before further Drone work",
            nameof(DysonAgentSystemPrompts.DroneDirective));
        MustNotContain(
            text,
            "WaitForSubagent only when those Explore reports block the next automatic turn",
            nameof(DysonAgentSystemPrompts.DroneDirective));
        MustNotContain(
            text,
            "Wait only when an Explore child’s output blocks the next automatic turn",
            nameof(DysonAgentSystemPrompts.DroneDirective));
    }

    private static void AssertDroneFirstTurnContextMandateExploreAlwaysWait()
    {
        var text = DysonAgentSystemPrompts.DroneFirstTurnContextMandate;
        MustContain(
            text,
            "WaitForSubagent on a later stage of the same turn and do no further work until those reports return",
            nameof(DysonAgentSystemPrompts.DroneFirstTurnContextMandate));
        MustNotContain(
            text,
            "WaitForSubagent only when those Explore reports block the next automatic turn",
            nameof(DysonAgentSystemPrompts.DroneFirstTurnContextMandate));
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

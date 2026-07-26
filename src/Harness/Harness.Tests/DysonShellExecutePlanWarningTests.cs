using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only Plan-mode ShellExecute soft warning gate (Xunit Fact).
/// 
/// </summary>
public class DysonShellExecutePlanWarningTests
{
    [Fact]
    public void Run()
    {
        AssertDescriptions();
        AssertConfigureForMode();
        AssertPreambleHelper();
    }

    private static void AssertDescriptions()
    {
        var shells = new[] { DysonShellType.Pwsh };
        var nonPlan = DysonMcpPipeline.CreateShellExecuteTool(shells, planMode: false);
        var plan = DysonMcpPipeline.CreateShellExecuteTool(shells, planMode: true);

        if (nonPlan is null || plan is null)
            throw new InvalidOperationException("CreateShellExecuteTool must return a tool when shells are available.");

        if (nonPlan.Description.Contains(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal))
            throw new InvalidOperationException("Non-Plan ShellExecute description must not include Plan warning.");

        if (!plan.Description.Contains(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal))
            throw new InvalidOperationException("Plan ShellExecute description must include Plan warning.");
    }

    private static void AssertConfigureForMode()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(
            DysonMcpAccessMode.FullAccess,
            [DysonShellType.Pwsh]);

        pipeline.ConfigureShellExecuteForMode(planMode: true);
        if (!pipeline.Tools.TryGetValue("ShellExecute", out var planTool)
            || !planTool.Description.Contains(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConfigureShellExecuteForMode(true) must set Plan warning description.");
        }

        if (!pipeline.Tools.TryGetValue("StartLongRunningShell", out var planStart)
            || !planStart.Description.Contains(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConfigureShellExecuteForMode(true) must Plan-warn StartLongRunningShell.");
        }

        pipeline.ConfigureShellExecuteForMode(planMode: false);
        if (!pipeline.Tools.TryGetValue("ShellExecute", out var workTool)
            || workTool.Description.Contains(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConfigureShellExecuteForMode(false) must clear Plan warning description.");
        }

        if (!pipeline.Tools.TryGetValue("StartLongRunningShell", out var workStart)
            || workStart.Description.Contains(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ConfigureShellExecuteForMode(false) must clear Plan warning on StartLongRunningShell.");
        }
    }

    private static void AssertPreambleHelper()
    {
        const string body = "exitCode=0";
        var plain = DysonMcpPipeline.PrefixPlanShellWarning(planMode: false, body);
        if (plain != body)
            throw new InvalidOperationException("Non-Plan preamble helper must leave content unchanged.");

        var prefixed = DysonMcpPipeline.PrefixPlanShellWarning(planMode: true, body);
        if (!prefixed.StartsWith(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal)
            || !prefixed.EndsWith(body, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Plan preamble helper must prefix WARNING then content.");
        }
    }
}

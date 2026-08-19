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
        AssertPythonNodeSnippetSentences();
    }

    private static void AssertDescriptions()
    {
        var shells = new[] { "Pwsh" };
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
            ["Pwsh"]);

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

    private static void AssertPythonNodeSnippetSentences()
    {
        const string pythonSentence =
            "When shell is Python, command is a raw Python snippet (passed to `-c`), not a file path or shell command line.";
        const string nodeSentence =
            "When shell is Node, command is a raw JavaScript snippet (passed to `-e`), not a file path or shell command line.";
        const string bothSentence = pythonSentence + " " + nodeSentence;
        const string pythonSchemaClause = "For Python, pass a raw Python snippet (`-c`).";
        const string nodeSchemaClause = "For Node, pass a raw JavaScript snippet (`-e`).";
        const string shellExecuteCommandBase = "Command line to execute in the chosen shell.";
        const string startCommandBase = "Command line to run in the background.";

        var pwsh = DysonMcpPipeline.CreateShellExecuteTool(["Pwsh"], planMode: false)
            ?? throw new InvalidOperationException("Pwsh ShellExecute must exist.");
        AssertOmitsSnippetContract(pwsh.Description, "Pwsh ShellExecute description");
        AssertOmitsSnippetContract(pwsh.InputSchemaJson, "Pwsh ShellExecute schema");
        if (!pwsh.InputSchemaJson.Contains(shellExecuteCommandBase, StringComparison.Ordinal))
            throw new InvalidOperationException("Pwsh ShellExecute schema must keep the base command description.");

        var pwshStart = DysonMcpPipeline.CreateLongRunningShellTools(["Pwsh"], planMode: false)
            .First(t => t.Name == "StartLongRunningShell");
        AssertOmitsSnippetContract(pwshStart.Description, "Pwsh StartLongRunningShell description");
        AssertOmitsSnippetContract(pwshStart.InputSchemaJson, "Pwsh StartLongRunningShell schema");
        if (!pwshStart.InputSchemaJson.Contains(startCommandBase, StringComparison.Ordinal))
            throw new InvalidOperationException("Pwsh StartLongRunningShell schema must keep the base command description.");

        AssertExactSnippetCopy(["Python", "Node"], bothSentence, pythonSchemaClause + " " + nodeSchemaClause, planMode: false);
        AssertExactSnippetCopy(["Python"], pythonSentence, pythonSchemaClause, planMode: false);
        AssertExactSnippetCopy(["Node"], nodeSentence, nodeSchemaClause, planMode: false);
        AssertExactSnippetCopy(["Python", "Node"], bothSentence, pythonSchemaClause + " " + nodeSchemaClause, planMode: true);

        static void AssertOmitsSnippetContract(string text, string label)
        {
            if (text.Contains("snippet", StringComparison.OrdinalIgnoreCase)
                || text.Contains("-c", StringComparison.Ordinal)
                || text.Contains("-e", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{label} must not mention Python/Node snippet contract.");
            }
        }

        void AssertExactSnippetCopy(
            string[] names,
            string snippetSentences,
            string schemaClauses,
            bool planMode)
        {
            var listed = string.Join(", ", names);
            var shellExecuteExpected =
                "Run a command in the session work directory. " +
                $"Available shells for this session: {listed}. " +
                "You must pass shell as one of these. Prefer dedicated MCP file tools over shell when they fit. " +
                snippetSentences;
            var startExpected =
                "Recommended for E2E test runs, large application builds, and keeping development servers running. " +
                "Start a background long-running shell in the session work directory. " +
                $"Available shells: {listed}. Returns longRunningShellId and the first ~1s of combined output. " +
                "Use ListLongRunningShells / ReadLongRunningShellTail / LongRunningShellInteract / " +
                "SubscribeToLongRunningShellCompletion / RequestLongRunningShellCancellation / AbortLongRunningShell to manage it. " +
                "Not persisted across UI restart (orphans OS processes). Prefer ShellExecute for one-shot commands. " +
                snippetSentences;
            if (planMode)
            {
                shellExecuteExpected += " " + DysonMcpPipeline.PlanShellExecuteWarning;
                startExpected += " " + DysonMcpPipeline.PlanShellExecuteWarning;
            }

            var shell = DysonMcpPipeline.CreateShellExecuteTool(names, planMode)
                ?? throw new InvalidOperationException($"{listed} ShellExecute must exist.");
            AssertExactDescription(shell.Description, shellExecuteExpected, $"{listed} ShellExecute");
            AssertSnippetBeforePlanWarning(shell.Description, snippetSentences, $"{listed} ShellExecute");
            AssertCommandSchema(
                shell.InputSchemaJson,
                shellExecuteCommandBase + " " + schemaClauses,
                schemaClauses,
                $"{listed} ShellExecute");

            var start = DysonMcpPipeline.CreateLongRunningShellTools(names, planMode)
                .First(t => t.Name == "StartLongRunningShell");
            AssertExactDescription(start.Description, startExpected, $"{listed} StartLongRunningShell");
            AssertSnippetBeforePlanWarning(start.Description, snippetSentences, $"{listed} StartLongRunningShell");
            AssertCommandSchema(
                start.InputSchemaJson,
                startCommandBase + " " + schemaClauses,
                schemaClauses,
                $"{listed} StartLongRunningShell");
        }

        static void AssertExactDescription(string actual, string expected, string label)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{label} description must match the exact Python/Node snippet copy.{Environment.NewLine}" +
                    $"Expected: {expected}{Environment.NewLine}Actual: {actual}");
            }
        }

        static void AssertSnippetBeforePlanWarning(string description, string snippetSentences, string label)
        {
            var snippetIndex = description.IndexOf(snippetSentences, StringComparison.Ordinal);
            if (snippetIndex < 0)
                throw new InvalidOperationException($"{label} must include the exact snippet sentence(s).");

            var warningIndex = description.IndexOf(DysonMcpPipeline.PlanShellExecuteWarning, StringComparison.Ordinal);
            if (warningIndex >= 0 && warningIndex < snippetIndex + snippetSentences.Length)
            {
                throw new InvalidOperationException(
                    $"{label} Plan warning must appear after the snippet sentence(s).");
            }
        }

        void AssertCommandSchema(
            string schemaJson,
            string expectedCommandDescription,
            string presentClauses,
            string label)
        {
            if (!schemaJson.Contains(expectedCommandDescription, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{label} command schema must include the matching raw snippet clause(s).{Environment.NewLine}" +
                    $"Expected to contain: {expectedCommandDescription}{Environment.NewLine}Actual: {schemaJson}");
            }

            if (!presentClauses.Contains(pythonSchemaClause, StringComparison.Ordinal)
                && schemaJson.Contains(pythonSchemaClause, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{label} command schema must omit the Python snippet clause.");
            }

            if (!presentClauses.Contains(nodeSchemaClause, StringComparison.Ordinal)
                && schemaJson.Contains(nodeSchemaClause, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{label} command schema must omit the Node snippet clause.");
            }
        }
    }
}

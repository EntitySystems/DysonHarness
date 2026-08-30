using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Tool-row parse / collapsed summary helpers (Xunit).</summary>
public class DysonToolCallUiTests
{
    [Fact]
    public void Run()
    {
        var writeArgs = """
            {"path":"src/A.cs","edits":[{"old_text":"a\nb\nc","new_text":"a\nx\ny\nz"},{"old_text":"old","new_text":"new1\nnew2"}]}
            """;
        var write = DysonToolCallUi.TryParseWriteFile(writeArgs)
            ?? throw new InvalidOperationException("TryParseWriteFile must parse edits.");
        if (write.Path != "src/A.cs" || write.LinesAdded != 6 || write.LinesRemoved != 4 || write.EditCount != 2)
            throw new InvalidOperationException($"WriteFile edit deltas mismatch: +{write.LinesAdded} -{write.LinesRemoved} edits={write.EditCount}");

        var rewrite = DysonToolCallUi.TryParseWriteFile("""{"path":"f.txt","content":"one\ntwo\nthree"}""")
            ?? throw new InvalidOperationException("TryParseWriteFile must parse content rewrite.");
        if (!rewrite.IsFullRewrite || rewrite.LinesAdded != 3 || rewrite.LinesRemoved != 0)
            throw new InvalidOperationException("WriteFile full rewrite must be +N only.");

        var writeSummary = DysonToolCallUi.GetCollapsedSummary("WriteFile", writeArgs, null, hasResult: false);
        if (!writeSummary.HasLineDelta || writeSummary.LinesAdded != 6 || writeSummary.LinesRemoved != 4)
            throw new InvalidOperationException("WriteFile collapsed summary must expose line deltas.");
        if (writeSummary.Text != "A.cs")
            throw new InvalidOperationException("WriteFile collapsed summary must include truncated basename.");

        var shellArgs = """{"shell":"pwsh","command":"dotnet build","workingDirectory":"src"}""";
        var shellResult =
            DysonMcpPipeline.PlanShellExecuteWarning
            + "\n\nexitCode=1 timedOut=true\n--- stdout ---\nhello\n--- stderr ---\nboom";
        var shell = DysonToolCallUi.ParseShellExecute(shellArgs, shellResult);
        if (shell.Shell != "pwsh"
            || shell.Command != "dotnet build"
            || shell.WorkingDirectory != "src"
            || shell.ExitCode != 1
            || !shell.TimedOut
            || shell.Stdout != "hello"
            || shell.Stderr != "boom"
            || string.IsNullOrEmpty(shell.PlanWarning))
        {
            throw new InvalidOperationException("ShellExecute parse mismatch.");
        }

        var shellSummary = DysonToolCallUi.GetCollapsedSummary("ShellExecute", shellArgs, shellResult, hasResult: true);
        if (shellSummary.Text is null
            || !shellSummary.Text.Contains("pwsh", StringComparison.Ordinal)
            || !shellSummary.Text.Contains("dotnet build", StringComparison.Ordinal)
            || !shellSummary.Text.Contains("exit 1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ShellExecute summary mismatch: {shellSummary.Text}");
        }

        var lrsArgs = """{"shell":"pwsh","command":"dotnet run --urls http://localhost:5180"}""";
        var lrsResult = "longRunningShellId=3\nstatus=Running\nshell=Pwsh\ncommand=dotnet run";
        var lrsSummary = DysonToolCallUi.GetCollapsedSummary("StartLongRunningShell", lrsArgs, lrsResult, hasResult: true);
        if (lrsSummary.Text is null
            || !lrsSummary.Text.Contains("#3", StringComparison.Ordinal)
            || !lrsSummary.Text.Contains("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"StartLongRunningShell summary mismatch: {lrsSummary.Text}");
        }

        if (DysonToolCallUi.Basename("src/foo/bar.cs") != "bar.cs")
            throw new InvalidOperationException("Basename failed.");
        if (DysonToolCallUi.GetString("""{"path":"a/b"}""", "path") != "a/b")
            throw new InvalidOperationException("GetString path extraction failed.");
        if (DysonToolCallUi.GithubOwnerRepo("https://github.com/acme/widget") != "acme/widget")
            throw new InvalidOperationException("GithubOwnerRepo failed.");
        if (DysonToolCallUi.CountGrepMatches("src/A.cs:12:hit\nNo matches.") != 1)
            throw new InvalidOperationException("CountGrepMatches failed.");
        if (DysonToolCallUi.CountLines("a\nb") != 2
            || DysonToolCallUi.CountLines("") != 0
            || DysonToolCallUi.CountLines("solo") != 1)
        {
            throw new InvalidOperationException("CountLines failed.");
        }

        var tempFileSummary = DysonToolCallUi.GetCollapsedSummary(
            "CreateFile",
            """{"path":"chart.css","content":"body {}","isTempFile":true}""",
            """{"path":".dyson/temp/chart-123.css","isTempFile":true}""",
            hasResult: true);
        if (tempFileSummary.Text is null || !tempFileSummary.Text.Contains("temp chart-123.css", StringComparison.Ordinal))
            throw new InvalidOperationException($"Temp CreateFile summary mismatch: {tempFileSummary.Text}");

        var renderArgs = """{"title":"Quarterly revenue","html":{"tempFile":".dyson/temp/chart.html"},"css":{"content":""},"js":{"content":""}}""";
        var render = DysonToolCallUi.TryParseHtmlVisualization(renderArgs)
            ?? throw new InvalidOperationException("RenderHtmlVisualization arguments must parse.");
        if (render.Title != "Quarterly revenue"
            || render.HtmlSource != "temp file: .dyson/temp/chart.html"
            || render.CssSource != "raw content"
            || render.JavaScriptSource != "raw content")
        {
            throw new InvalidOperationException("RenderHtmlVisualization parsed source mismatch.");
        }

        var renderSummary = DysonToolCallUi.GetCollapsedSummary("RenderHtmlVisualization", renderArgs, null, hasResult: false);
        if (renderSummary.Text != "Quarterly revenue")
            throw new InvalidOperationException($"RenderHtmlVisualization summary mismatch: {renderSummary.Text}");

        // SubmitSubagentReport task-failed handoff is not a tool error — collapsed copy must not say "report failed".
        var failedHandoff = DysonToolCallUi.GetCollapsedSummary(
            "SubmitSubagentReport",
            """{"summary":"blocked: missing schema","status":"failed"}""",
            resultContent: null,
            hasResult: false);
        if (failedHandoff.Text is null
            || failedHandoff.Text.Contains("report failed", StringComparison.OrdinalIgnoreCase)
            || !failedHandoff.Text.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || !failedHandoff.Text.Contains("handoff", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SubmitSubagentReport failed-status summary must read as handoff outcome, got: {failedHandoff.Text}");
        }

        var completedHandoff = DysonToolCallUi.GetCollapsedSummary(
            "SubmitSubagentReport",
            """{"summary":"done","status":"completed"}""",
            resultContent: null,
            hasResult: false);
        if (completedHandoff.Text != "report submitted")
        {
            throw new InvalidOperationException(
                $"SubmitSubagentReport completed summary mismatch: {completedHandoff.Text}");
        }

        var reportArgs = """{"summary":"# Report\n\nDone.","status":"completed"}""";
        var reportResult = """{"ok":true}""";
        var reportFile = DysonToolCallUi.TryResolveViewAsFile(
            "SubmitSubagentReport",
            "call-1",
            reportArgs,
            reportResult)
            ?? throw new InvalidOperationException("SubmitSubagentReport must resolve a view-as-file payload.");
        if (reportFile.DisplayPath != "tool-calls/SubmitSubagentReport-call-1.md"
            || reportFile.Content != "# Report\n\nDone.")
        {
            throw new InvalidOperationException(
                $"SubmitSubagentReport view-as-file must open arguments summary as markdown, got {reportFile.DisplayPath}: {reportFile.Content}");
        }

        var resultSummary = DysonToolCallUi.TryResolveViewAsFile(
            "FreeSearch",
            null,
            """{"query":"x"}""",
            """{"summary":"top hits"}""")
            ?? throw new InvalidOperationException("Result summary must resolve.");
        if (resultSummary.DisplayPath != "tool-calls/FreeSearch.md" || resultSummary.Content != "top hits")
            throw new InvalidOperationException("Result-content summary must open as markdown without a call id suffix.");

        var argsWin = DysonToolCallUi.TryResolveViewAsFile(
            "CompleteTask",
            "c2",
            """{"summary":"from args"}""",
            """{"summary":"from result"}""")
            ?? throw new InvalidOperationException("CompleteTask must resolve.");
        if (argsWin.Content != "from args")
            throw new InvalidOperationException("Arguments summary must win over result summary.");

        var rawResult = DysonToolCallUi.TryResolveViewAsFile(
            "ShellExecute",
            "s1",
            """{"shell":"pwsh","command":"ls"}""",
            "exitCode=0\n--- stdout ---\nok")
            ?? throw new InvalidOperationException("Raw result must resolve.");
        if (rawResult.DisplayPath != "tool-calls/ShellExecute-s1.json"
            || rawResult.Content != "exitCode=0\n--- stdout ---\nok")
        {
            throw new InvalidOperationException("Tools without a summary field must fall back to raw result JSON.");
        }

        if (DysonToolCallUi.TryResolveViewAsFile("ReadFile", null, """{"path":"a"}""", null) is not null)
            throw new InvalidOperationException("No summary and no result must be null.");
        if (DysonToolCallUi.TryResolveViewAsFile("X", null, """{"summary":"   "}""", null) is not null)
            throw new InvalidOperationException("Whitespace-only summary must be ignored.");
    }
}

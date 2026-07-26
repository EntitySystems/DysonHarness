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
        if (!string.IsNullOrEmpty(writeSummary.Text))
            throw new InvalidOperationException("WriteFile collapsed summary must not include path text.");

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
    }
}

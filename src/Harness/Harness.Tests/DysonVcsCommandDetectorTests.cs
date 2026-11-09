using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Pure <see cref="DysonVcsCommandDetector"/> cases from the pipelines plan (section 6).
/// </summary>
public class DysonVcsCommandDetectorTests
{
    [Theory]
    [InlineData("PowerShell", "gh run list", true)]
    [InlineData("PowerShell", "git push", true)]
    [InlineData("PowerShell", "GH api x", true)]
    [InlineData("PowerShell", "\"C:\\Program Files\\GitHub CLI\\gh.exe\" api x", true)]
    [InlineData("PowerShell", "C:\\tools\\git.exe status", true)]
    [InlineData("bash", "/usr/bin/git fetch", true)]
    [InlineData("bash", "cd src && git push", true)]
    [InlineData("bash", "npm test; gh pr create", true)]
    [InlineData("PowerShell", "Get-Content p.diff | git apply", true)]
    [InlineData("PowerShell", "& gh run watch", true)]
    [InlineData("PowerShell", "& \"C:\\Program Files\\Git\\cmd\\git.exe\" log", true)]
    [InlineData("bash", "FOO=1 git commit -m x", true)]
    [InlineData("bash", "echo hi\ngit status", true)]
    [InlineData("Python", "subprocess.run([\"gh\",\"api\",\"x\"])", true)]
    [InlineData("Node", "execSync('git push')", true)]
    [InlineData("PowerShell", "digit", false)]
    [InlineData("bash", "cat github-cli-notes.txt", false)]
    [InlineData("PowerShell", "Get-Content gh-notes.md", false)]
    [InlineData("bash", "echo git", false)]
    [InlineData("bash", "gitk", false)]
    [InlineData("bash", "git-lfs pull", false)]
    [InlineData("bash", "mygit push", false)]
    [InlineData("PowerShell", ".\\gh.ps1", false)]
    [InlineData("PowerShell", "Get-ChildItem .git", false)]
    [InlineData("PowerShell", "dotnet build", false)]
    [InlineData("PowerShell", "", false)]
    [InlineData("PowerShell", "   ", false)]
    [InlineData("Python", "print(\"digit\")", false)]
    public void InvokesVcs_matches_the_plan_cases(string shell, string command, bool expected) =>
        Assert.Equal(expected, DysonVcsCommandDetector.InvokesVcs(shell, command));
}

using DysonHarness;

namespace Harness.Tests;

public class DysonExecutableLocatorTests
{
    [Fact]
    public void Find_WindowsPathExt_FindsGhExe()
    {
        if (!OperatingSystem.IsWindows())
            return; // PATHEXT is Windows-only; Find_PathMiss_FallsBackToExtraDir covers the bare name.

        var seen = new List<string>();
        string? found = DysonExecutableLocator.Find(
            "gh",
            [],
            @"C:\tools;C:\git",
            ".EXE;.CMD;.BAT",
            path =>
            {
                seen.Add(path);
                return path.Equals(@"C:\git\gh.EXE", StringComparison.OrdinalIgnoreCase);
            });

        Assert.NotNull(found);
        Assert.EndsWith(
            Path.Combine("git", "gh.EXE"),
            found,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(seen, p => p.EndsWith(@"tools\gh.EXE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(seen, p => p.EndsWith(@"git\gh.EXE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(seen, p => p.EndsWith(@"git\gh.CMD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Find_PathMiss_FallsBackToExtraDir()
    {
        // Windows appends PATHEXT; Linux/macOS probe the bare name.
        var leaf = OperatingSystem.IsWindows() ? "gh.EXE" : "gh";
        var extraDir = Path.Combine(Path.GetTempPath(), "GitHub CLI");
        string? found = DysonExecutableLocator.Find(
            "gh",
            [extraDir],
            Path.Combine(Path.GetTempPath(), "empty"),
            ".EXE",
            path => path == Path.Combine(extraDir, leaf));

        Assert.Equal(Path.Combine(extraDir, leaf), found);
    }

    [Fact]
    public void Find_Nowhere_ReturnsNull()
    {
        string? found = DysonExecutableLocator.Find(
            "gh",
            [@"D:\also-missing"],
            @"C:\missing",
            ".EXE;.CMD;.BAT",
            _ => false);

        Assert.Null(found);
    }
}

using DysonHarness;

namespace Harness.Tests;

public class DysonExecutableLocatorTests
{
    [Fact]
    public void Find_WindowsPathExt_FindsGhExe()
    {
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
        string? found = DysonExecutableLocator.Find(
            "gh",
            [@"C:\Program Files\GitHub CLI"],
            @"C:\empty",
            ".EXE",
            path => path.Contains("GitHub CLI", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("gh.EXE", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(found);
        Assert.Contains("GitHub CLI", found, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("gh.EXE", found, StringComparison.OrdinalIgnoreCase);
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

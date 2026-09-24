using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Fails if the Blazor UI blocks the circuit on sync git or an unbounded wait.
/// Bare <c>.Result</c> is not scanned (<c>Tracked.Result</c> and similar are properties).
/// </summary>
public class DysonUiSyncGitCallScanTests
{
    private static readonly string[] Banned =
    [
        "GetAwaiter().GetResult()",
        ".Wait(",
        "DysonGitInfo.TryGetBranch(",
        "DysonGitInfo.TryGetOrigin(",
        "DysonGitInfo.TryGetStatusPorcelain(",
        "DysonGitInfo.TryFindRootMostRepo(",
        "DysonSessionWorktree.Ensure(",
        "DysonSessionWorktree.Remove(",
        "DysonSessionWorktree.Merge(",
    ];

    [Fact]
    public void HarnessUi_has_no_sync_git_or_blocking_wait()
    {
        var root = FindUiProjectRoot();
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            if (ext is not ".cs" and not ".razor")
                continue;

            var name = Path.GetFileName(file);
            var allowBlockingWait = name.Equals("DysonUiWebHost.cs", StringComparison.OrdinalIgnoreCase);
            var text = File.ReadAllText(file);
            foreach (var token in Banned)
            {
                if (allowBlockingWait && token is "GetAwaiter().GetResult()" or ".Wait(")
                    continue;

                if (text.Contains(token, StringComparison.Ordinal))
                    failures.Add($"{Path.GetRelativePath(root, file)}: {token}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindUiProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Harness", "Harness.UI");
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/Harness/Harness.UI from the test output directory.");
    }
}

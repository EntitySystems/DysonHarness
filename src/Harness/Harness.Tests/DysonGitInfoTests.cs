using System.Diagnostics;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Porcelain parse + outermost .git discovery.</summary>
public class DysonGitInfoTests
{
    [Fact]
    public void ParsePorcelain_groups_A_M_D_and_untracked()
    {
        var stdout = """
            A  src/new.cs
             M src/edit.cs
            MM src/both.cs
             D src/gone.cs
            R  old.txt -> src/renamed.txt
            C  template.md -> src/copy.md
            ?? untracked/file.txt
            !! ignored.bin
            """;

        var entries = DysonGitInfo.ParsePorcelain(stdout);

        Assert.Equal(
            [
                ("src/new.cs", DysonGitChangeKind.Added),
                ("src/both.cs", DysonGitChangeKind.Modified),
                ("src/copy.md", DysonGitChangeKind.Modified),
                ("src/edit.cs", DysonGitChangeKind.Modified),
                ("src/renamed.txt", DysonGitChangeKind.Modified),
                ("src/gone.cs", DysonGitChangeKind.Deleted),
                ("untracked/file.txt", DysonGitChangeKind.Untracked),
            ],
            entries.Select(e => (e.Path, e.Kind)).ToArray());
    }

    [Fact]
    public void ParsePorcelain_prefers_added_over_deleted_when_both_present()
    {
        var entries = DysonGitInfo.ParsePorcelain("AD conflict.txt\n");
        Assert.Single(entries);
        Assert.Equal(DysonGitChangeKind.Added, entries[0].Kind);
        Assert.Equal("conflict.txt", entries[0].Path);
    }

    [Fact]
    public void TryFindRootMostRepo_picks_outermost_git()
    {
        var outer = Path.Combine(Path.GetTempPath(), "dyson-git-" + Guid.NewGuid().ToString("N"));
        var mid = Path.Combine(outer, "mid");
        var inner = Path.Combine(mid, "inner");
        Directory.CreateDirectory(inner);
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        try
        {
            var fromInner = DysonGitInfo.TryFindRootMostRepo(inner);
            Assert.True(fromInner.IsSuccess, fromInner.IsError ? fromInner.Error : null);
            Assert.Equal(Path.GetFullPath(outer), Path.GetFullPath(fromInner.Value));

            var fromMid = DysonGitInfo.TryFindRootMostRepo(mid);
            Assert.True(fromMid.IsSuccess, fromMid.IsError ? fromMid.Error : null);
            Assert.Equal(Path.GetFullPath(outer), Path.GetFullPath(fromMid.Value));
        }
        finally
        {
            try
            {
                Directory.Delete(outer, recursive: true);
            }
            catch
            {
                // ignore cleanup races
            }
        }
    }

    [Fact]
    public void TryFindRootMostRepo_accepts_gitfile_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-gitfile-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /somewhere/else");

        try
        {
            var found = DysonGitInfo.TryFindRootMostRepo(nested);
            Assert.True(found.IsSuccess, found.IsError ? found.Error : null);
            Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(found.Value));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public void TryFindRootMostRepo_errors_when_no_git()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var found = DysonGitInfo.TryFindRootMostRepo(root);
            Assert.True(found.IsError);
            Assert.Contains("No git", found.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public void TryGetStatusPorcelain_large_output_succeeds_without_pipe_deadlock()
    {
        // Enough untracked lines to exceed a typical OS pipe buffer (~4KB) if reads waited
        // until after WaitForExit — classic Process redirect deadlock.
        const int fileCount = 800;
        var root = Path.Combine(Path.GetTempPath(), "dyson-git-large-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            RunGitOrThrow(root, ["init"]);
            for (var i = 0; i < fileCount; i++)
                File.WriteAllText(Path.Combine(root, $"u{i:D4}.txt"), "x");

            var status = DysonGitInfo.TryGetStatusPorcelain(root);
            Assert.True(status.IsSuccess, status.IsError ? status.Error : null);
            Assert.True(status.Value.Count >= fileCount, $"expected >= {fileCount} entries, got {status.Value.Count}");
            Assert.All(status.Value, e => Assert.Equal(DysonGitChangeKind.Untracked, e.Kind));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup races
            }
        }
    }

    private static void RunGitOrThrow(string workingDirectory, string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(workingDirectory);
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        if (!process.Start())
            throw new InvalidOperationException("Failed to start git for test setup.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException("git setup timed out.");
        }

        Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {(string.Join(' ', args))} failed: {stderrTask.Result}");
    }
}

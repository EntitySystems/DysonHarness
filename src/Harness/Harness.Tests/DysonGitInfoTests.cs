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

    [Theory]
    [InlineData("https://github.com/acme/repo.git", DysonGitProvider.GitHub)]
    [InlineData("git@github.com:acme/repo.git", DysonGitProvider.GitHub)]
    [InlineData("ssh://git@github.com/acme/repo.git", DysonGitProvider.GitHub)]
    [InlineData("git://github.com/acme/repo.git", DysonGitProvider.GitHub)]
    [InlineData("https://github.mycompany.com/acme/repo.git", DysonGitProvider.GitHub)]
    [InlineData("https://gitlab.com/acme/repo.git", DysonGitProvider.GitLab)]
    [InlineData("git@gitlab.com:acme/repo.git", DysonGitProvider.GitLab)]
    [InlineData("ssh://git@gitlab.com/acme/repo.git", DysonGitProvider.GitLab)]
    [InlineData("https://gitlab.mycompany.com/acme/repo.git", DysonGitProvider.GitLab)]
    [InlineData("https://dev.azure.com/org/project/_git/repo", DysonGitProvider.AzureDevOps)]
    [InlineData("git@ssh.dev.azure.com:v3/org/project/repo", DysonGitProvider.AzureDevOps)]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/org/project/repo", DysonGitProvider.AzureDevOps)]
    [InlineData("https://contoso.visualstudio.com/project/_git/repo", DysonGitProvider.AzureDevOps)]
    [InlineData("https://cursor.com/origin/repo", DysonGitProvider.CursorOrigin)]
    [InlineData("git@cursor.com:org/repo.git", DysonGitProvider.CursorOrigin)]
    [InlineData("ssh://git@cursor.com/org/repo.git", DysonGitProvider.CursorOrigin)]
    [InlineData("https://example.cursor.com/repo", DysonGitProvider.CursorOrigin)]
    [InlineData("https://github.com/cursor/origin-sync.git", DysonGitProvider.GitHub)]
    [InlineData("https://bitbucket.org/acme/repo.git", DysonGitProvider.Other)]
    [InlineData("git@git.example.com:acme/repo.git", DysonGitProvider.Other)]
    [InlineData(null, DysonGitProvider.None)]
    [InlineData("", DysonGitProvider.None)]
    [InlineData("   ", DysonGitProvider.None)]
    [InlineData("not a url", DysonGitProvider.None)]
    [InlineData("https://", DysonGitProvider.None)]
    public void ClassifyProvider_maps_origin_host(string? origin, DysonGitProvider expected)
    {
        Assert.Equal(expected, DysonGitInfo.ClassifyProvider(origin));
    }

    [Fact]
    public void ToStoredSlug_maps_enum_values()
    {
        Assert.Null(DysonGitInfo.ToStoredSlug(DysonGitProvider.None));
        Assert.Equal("github", DysonGitInfo.ToStoredSlug(DysonGitProvider.GitHub));
        Assert.Equal("gitlab", DysonGitInfo.ToStoredSlug(DysonGitProvider.GitLab));
        Assert.Equal("azure-devops", DysonGitInfo.ToStoredSlug(DysonGitProvider.AzureDevOps));
        Assert.Equal("cursor-origin", DysonGitInfo.ToStoredSlug(DysonGitProvider.CursorOrigin));
        Assert.Equal("other", DysonGitInfo.ToStoredSlug(DysonGitProvider.Other));
        Assert.Equal(DysonGitProvider.None, DysonGitInfo.FromStoredSlug(null));
        Assert.Equal(DysonGitProvider.GitHub, DysonGitInfo.FromStoredSlug("github"));
        Assert.Equal(DysonGitProvider.CursorOrigin, DysonGitInfo.FromStoredSlug("cursor-origin"));
        Assert.Equal(DysonGitProvider.None, DysonGitInfo.FromStoredSlug("unknown"));
    }

    [Fact]
    public void TryGetOrigin_reads_outermost_remote()
    {
        var outer = Path.Combine(Path.GetTempPath(), "dyson-git-origin-" + Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(outer, "nested");
        Directory.CreateDirectory(inner);

        try
        {
            RunGitOrThrow(outer, ["init"]);
            RunGitOrThrow(outer, ["remote", "add", "origin", "https://github.com/acme/repo.git"]);

            var fromInner = DysonGitInfo.TryGetOrigin(inner);
            Assert.True(fromInner.IsSuccess, fromInner.IsError ? fromInner.Error : null);
            Assert.Equal("https://github.com/acme/repo.git", fromInner.Value);
            Assert.Equal(DysonGitProvider.GitHub, DysonGitInfo.ClassifyProvider(fromInner.Value));
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
    public void TryGetOrigin_errors_when_no_origin()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-git-no-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            RunGitOrThrow(root, ["init"]);
            var origin = DysonGitInfo.TryGetOrigin(root);
            Assert.True(origin.IsError);
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

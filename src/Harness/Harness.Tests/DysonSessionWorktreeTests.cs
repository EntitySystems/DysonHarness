using System.Diagnostics;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Session worktree ensure/merge/remove and prompt suffix (temp repos only).</summary>
public class DysonSessionWorktreeTests
{
    [Fact]
    public void FormatBranch_and_ResolveWorktreeAbsolutePath_use_sibling_layout()
    {
        var sessionId = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
        Assert.Equal("dyson/abcdef01", DysonSessionWorktree.FormatBranch(sessionId));

        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "sample-repo");
        Directory.CreateDirectory(repo);
        try
        {
            var resolved = DysonSessionWorktree.ResolveWorktreeAbsolutePath(repo, sessionId);
            Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
            var expected = Path.GetFullPath(Path.Combine(
                parent,
                "sample-repo.dyson-worktrees",
                sessionId.ToString("N")));
            Assert.True(SamePath(expected, resolved.Value), $"{resolved.Value} vs {expected}");
            Assert.False(
                resolved.Value.StartsWith(
                    Path.GetFullPath(repo) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        finally
        {
            DeleteQuiet(parent);
        }

        var empty = DysonSessionWorktree.ResolveWorktreeAbsolutePath("  ", sessionId);
        Assert.True(empty.IsError);
    }

    [Fact]
    public void Ensure_fails_when_workdir_is_not_a_git_repo()
    {
        var root = CreateTempDir();
        try
        {
            var result = DysonSessionWorktree.Ensure(root, Guid.NewGuid());
            Assert.True(result.IsError);
            Assert.Equal(DysonSessionWorktree.NotAGitRepositoryMessage, result.Error);
        }
        finally
        {
            DeleteQuiet(root);
        }
    }

    [Fact]
    public void Ensure_adds_worktree_copies_untracked_harness_files_and_is_idempotent()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        var sessionId = Guid.NewGuid();

        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "tracked.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            WriteAllLf(Path.Combine(repo, "openrules.json"), "{}\n");
            WriteAllLf(Path.Combine(repo, "AGENTS.md"), "# agents\n");
            WriteAllLf(Path.Combine(repo, ".dyson", "mcp", "x.json"), "{\"ok\":true}\n");
            WriteAllLf(Path.Combine(repo, ".dyson", "skills", "foo", "SKILL.md"), "# skill\n");
            WriteAllLf(Path.Combine(repo, ".dyson", "plans", "secret.md"), "plan\n");

            var first = DysonSessionWorktree.Ensure(repo, sessionId);
            Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
            Assert.Equal(DysonSessionWorktree.FormatBranch(sessionId), first.Value.Branch);

            var listed = DysonGitInfo.TryListWorktrees(repo);
            Assert.True(listed.IsSuccess, listed.IsError ? listed.Error : null);
            Assert.Contains(listed.Value, e => SamePath(e.Path, first.Value.AbsolutePath));

            var wt = first.Value.AbsolutePath;
            Assert.True(File.Exists(Path.Combine(wt, "openrules.json")));
            Assert.True(File.Exists(Path.Combine(wt, "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(wt, ".dyson", "mcp", "x.json")));
            Assert.True(File.Exists(Path.Combine(wt, ".dyson", "skills", "foo", "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(wt, ".dyson", "plans", "secret.md")));
            Assert.Equal("{}\n", File.ReadAllText(Path.Combine(wt, "openrules.json")));

            var second = DysonSessionWorktree.Ensure(repo, sessionId);
            Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
            Assert.True(SamePath(first.Value.AbsolutePath, second.Value.AbsolutePath));
            Assert.Equal(first.Value.Branch, second.Value.Branch);
        }
        finally
        {
            var path = DysonSessionWorktree.ResolveWorktreeAbsolutePath(repo, sessionId);
            if (path.IsSuccess)
                _ = DysonGitInfo.TryRemoveWorktree(repo, path.Value, force: true);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public void Merge_success_removes_worktree_and_updates_main_tree()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        var sessionId = Guid.NewGuid();

        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            var ensured = DysonSessionWorktree.Ensure(repo, sessionId);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            var wt = ensured.Value.AbsolutePath;
            var branch = ensured.Value.Branch;

            WriteAllLf(Path.Combine(wt, "file.txt"), "from-wt\n");
            RunGitOrThrow(wt, ["add", "-A"]);
            RunGitOrThrow(wt, ["commit", "-m", "wt"]);

            var merge = DysonSessionWorktree.Merge(repo, wt, branch);
            Assert.True(merge.IsSuccess, merge.IsError ? merge.Error : null);
            Assert.Equal("from-wt\n", File.ReadAllText(Path.Combine(repo, "file.txt")));

            var after = DysonGitInfo.TryListWorktrees(repo);
            Assert.True(after.IsSuccess, after.IsError ? after.Error : null);
            Assert.DoesNotContain(after.Value, e => SamePath(e.Path, wt));
        }
        finally
        {
            var path = DysonSessionWorktree.ResolveWorktreeAbsolutePath(repo, sessionId);
            if (path.IsSuccess)
                _ = DysonGitInfo.TryRemoveWorktree(repo, path.Value, force: true);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public void Merge_conflict_is_error_and_worktree_stays_listed()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        var sessionId = Guid.NewGuid();

        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            var ensured = DysonSessionWorktree.Ensure(repo, sessionId);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            var wt = ensured.Value.AbsolutePath;
            var branch = ensured.Value.Branch;

            WriteAllLf(Path.Combine(wt, "file.txt"), "from-wt\n");
            RunGitOrThrow(wt, ["add", "-A"]);
            RunGitOrThrow(wt, ["commit", "-m", "wt"]);

            WriteAllLf(Path.Combine(repo, "file.txt"), "from-main\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "main"]);

            var merge = DysonSessionWorktree.Merge(repo, wt, branch);
            Assert.True(merge.IsError);
            Assert.False(string.IsNullOrWhiteSpace(merge.Error));

            var listed = DysonGitInfo.TryListWorktrees(repo);
            Assert.True(listed.IsSuccess, listed.IsError ? listed.Error : null);
            Assert.Contains(listed.Value, e => SamePath(e.Path, wt));
        }
        finally
        {
            try
            {
                RunGitOrThrow(repo, ["merge", "--abort"]);
            }
            catch
            {
                // ignore if no merge in progress
            }

            var path = DysonSessionWorktree.ResolveWorktreeAbsolutePath(repo, sessionId);
            if (path.IsSuccess)
                _ = DysonGitInfo.TryRemoveWorktree(repo, path.Value, force: true);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public void Remove_force_clears_dirty_worktree()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        var sessionId = Guid.NewGuid();

        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            var ensured = DysonSessionWorktree.Ensure(repo, sessionId);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            var wt = ensured.Value.AbsolutePath;
            WriteAllLf(Path.Combine(wt, "file.txt"), "dirty\n");

            var withoutForce = DysonSessionWorktree.Remove(repo, wt);
            Assert.True(withoutForce.IsError);

            var withForce = DysonSessionWorktree.Remove(repo, wt, force: true);
            Assert.True(withForce.IsSuccess, withForce.IsError ? withForce.Error : null);

            var after = DysonGitInfo.TryListWorktrees(repo);
            Assert.True(after.IsSuccess, after.IsError ? after.Error : null);
            Assert.DoesNotContain(after.Value, e => SamePath(e.Path, wt));
        }
        finally
        {
            var path = DysonSessionWorktree.ResolveWorktreeAbsolutePath(repo, sessionId);
            if (path.IsSuccess)
                _ = DysonGitInfo.TryRemoveWorktree(repo, path.Value, force: true);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public void BuildWorktreePromptBlock_off_enabled_and_bound()
    {
        Assert.Null(DysonAgentSystemPrompts.BuildWorktreePromptBlock(
            enabled: false,
            worktreeAbsolutePath: @"C:\wt",
            worktreeBranch: "dyson/abcdef01",
            registeredAbsolutePath: @"C:\repo"));

        var pending = DysonAgentSystemPrompts.BuildWorktreePromptBlock(
            enabled: true,
            worktreeAbsolutePath: null,
            worktreeBranch: null,
            registeredAbsolutePath: @"C:\repo");
        Assert.Equal(DysonAgentSystemPrompts.WorktreeEnabledNotCreatedPromptBlock, pending);
        Assert.Contains("Git worktree (enabled, not created yet):", pending, StringComparison.Ordinal);
        Assert.Contains("Do not run `git worktree add`", pending, StringComparison.Ordinal);

        var bound = DysonAgentSystemPrompts.BuildWorktreePromptBlock(
            enabled: true,
            worktreeAbsolutePath: @"C:\repo.dyson-worktrees\abc",
            worktreeBranch: "dyson/abcdef01",
            registeredAbsolutePath: @"C:\repo");
        Assert.NotNull(bound);
        Assert.Contains("Git worktree (bound):", bound, StringComparison.Ordinal);
        Assert.Contains(@"C:\repo.dyson-worktrees\abc", bound, StringComparison.Ordinal);
        Assert.Contains("dyson/abcdef01", bound, StringComparison.Ordinal);
        Assert.Contains(@"C:\repo", bound, StringComparison.Ordinal);
        Assert.Contains("Native root:", bound, StringComparison.Ordinal);
        Assert.Contains("Registered project root", bound, StringComparison.Ordinal);
    }

    private static bool SamePath(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-session-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteQuiet(string root)
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

    private static void GitInit(string root)
    {
        RunGitOrThrow(root, ["init"]);
        RunGitOrThrow(root, ["config", "user.email", "dyson-tests@example.com"]);
        RunGitOrThrow(root, ["config", "user.name", "Dyson Tests"]);
        RunGitOrThrow(root, ["config", "commit.gpgsign", "false"]);
        RunGitOrThrow(root, ["config", "core.autocrlf", "false"]);
    }

    private static void WriteAllLf(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, contents);
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

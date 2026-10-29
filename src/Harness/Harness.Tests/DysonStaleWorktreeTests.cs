using System.Diagnostics;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Stale worktrees (git no longer lists the path) and locked leftover folders.
/// Temp repos only.
/// </summary>
public class DysonStaleWorktreeTests
{
    [Fact]
    public void IsPathUnder_requires_a_real_descendant()
    {
        var parent = CreateTempDir();
        try
        {
            var wt = Path.Combine(parent, "wt");
            var wt2 = Path.Combine(parent, "wt2");
            var inside = Path.Combine(wt, "bin", "testhost.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
            Directory.CreateDirectory(wt2);

            Assert.True(DysonGitInfo.IsPathUnder(inside, wt));
            Assert.True(DysonGitInfo.IsPathUnder(wt, wt));
            Assert.False(DysonGitInfo.IsPathUnder(wt2, wt));
            Assert.False(DysonGitInfo.IsPathUnder(Path.Combine(wt2, "bin", "testhost.exe"), wt));
            Assert.False(DysonGitInfo.IsPathUnder(parent, wt));
            Assert.False(DysonGitInfo.IsPathUnder(wt, parent + Path.DirectorySeparatorChar + "nope"));
            Assert.False(DysonGitInfo.IsPathUnder("", wt));
            Assert.False(DysonGitInfo.IsPathUnder(wt, ""));
            Assert.False(DysonGitInfo.IsPathUnder(wt, Path.GetPathRoot(wt)));
        }
        finally
        {
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Branch_ancestor_is_true_only_after_the_commits_are_in_HEAD()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        var worktree = Path.Combine(parent, "wt");
        Directory.CreateDirectory(repo);
        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            var branch = "dyson/anc" + Guid.NewGuid().ToString("N")[..8];
            var added = await DysonGitInfo.TryAddWorktreeAsync(repo, worktree, branch);
            Assert.True(added.IsSuccess, added.IsError ? added.Error : null);

            var atFork = await DysonGitInfo.TryIsBranchAncestorOfHeadAsync(repo, branch);
            Assert.True(atFork.IsSuccess, atFork.IsError ? atFork.Error : null);
            Assert.True(atFork.Value);

            WriteAllLf(Path.Combine(worktree, "file.txt"), "from-wt\n");
            RunGitOrThrow(worktree, ["add", "-A"]);
            RunGitOrThrow(worktree, ["commit", "-m", "wt"]);

            var ahead = await DysonSessionWorktree.TryIsBranchMergedAsync(repo, branch);
            Assert.True(ahead.IsSuccess, ahead.IsError ? ahead.Error : null);
            Assert.False(ahead.Value);

            var merge = await DysonGitInfo.TryMergeBranchAsync(repo, branch);
            Assert.True(merge.IsSuccess, merge.IsError ? merge.Error : null);

            var merged = await DysonSessionWorktree.TryIsBranchMergedAsync(repo, branch);
            Assert.True(merged.IsSuccess, merged.IsError ? merged.Error : null);
            Assert.True(merged.Value);

            var missing = await DysonGitInfo.TryIsBranchAncestorOfHeadAsync(repo, "dyson/does-not-exist");
            Assert.True(missing.IsSuccess, missing.IsError ? missing.Error : null);
            Assert.True(missing.Value);
        }
        finally
        {
            _ = await DysonGitInfo.TryRemoveWorktreeAsync(repo, worktree, force: true);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Remove_and_merge_succeed_when_git_no_longer_lists_the_worktree()
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

            var ensured = await DysonSessionWorktree.EnsureAsync(repo, sessionId);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            var wt = ensured.Value.AbsolutePath;
            var branch = ensured.Value.Branch;

            WriteAllLf(Path.Combine(wt, "file.txt"), "from-wt\n");
            RunGitOrThrow(wt, ["add", "-A"]);
            RunGitOrThrow(wt, ["commit", "-m", "wt"]);
            var mergeFirst = await DysonGitInfo.TryMergeBranchAsync(repo, branch);
            Assert.True(mergeFirst.IsSuccess, mergeFirst.IsError ? mergeFirst.Error : null);

            RunGitOrThrow(repo, ["worktree", "prune"]);
            Assert.True(Directory.Exists(wt));

            var removed = await DysonSessionWorktree.RemoveAsync(repo, wt, force: true);
            Assert.True(removed.IsSuccess, removed.IsError ? removed.Error : null);
            Assert.False(Directory.Exists(wt));

            var afterRemove = await DysonGitInfo.TryListWorktreesAsync(repo);
            Assert.True(afterRemove.IsSuccess, afterRemove.IsError ? afterRemove.Error : null);
            Assert.DoesNotContain(afterRemove.Value, e => SamePath(e.Path, wt));

            var again = await DysonSessionWorktree.EnsureAsync(repo, Guid.NewGuid());
            Assert.True(again.IsSuccess, again.IsError ? again.Error : null);
            var wt2 = again.Value.AbsolutePath;
            Directory.Delete(wt2, recursive: true);
            RunGitOrThrow(repo, ["worktree", "prune"]);

            var mergeGone = await DysonSessionWorktree.MergeAsync(repo, wt2, again.Value.Branch);
            Assert.True(mergeGone.IsSuccess, mergeGone.IsError ? mergeGone.Error : null);
            var listed = await DysonGitInfo.TryListWorktreesAsync(repo);
            Assert.True(listed.IsSuccess, listed.IsError ? listed.Error : null);
            Assert.DoesNotContain(listed.Value, e => SamePath(e.Path, wt2));
        }
        finally
        {
            var path = DysonSessionWorktree.ResolveWorktreeAbsolutePath(repo, sessionId);
            if (path.IsSuccess)
                _ = await DysonGitInfo.TryRemoveWorktreeAsync(repo, path.Value, force: true);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Stale_folder_delete_retries_until_the_lock_is_released()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        var worktree = Path.Combine(parent, "wt");
        Directory.CreateDirectory(repo);
        var held = Path.Combine(worktree, "locked.txt");
        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            var branch = "dyson/lock" + Guid.NewGuid().ToString("N")[..8];
            var added = await DysonGitInfo.TryAddWorktreeAsync(repo, worktree, branch);
            Assert.True(added.IsSuccess, added.IsError ? added.Error : null);
            WriteAllLf(held, "held\n");
            RunGitOrThrow(repo, ["worktree", "prune"]);

            await using var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
            var attempts = 0;
            DysonGitInfo.BeforeStaleFolderDelete = () =>
            {
                attempts++;
                if (attempts >= 2)
                    stream.Dispose();
            };

            var removed = await DysonGitInfo.TryRemoveWorktreeAsync(repo, worktree, force: true);
            Assert.True(removed.IsSuccess, removed.IsError ? removed.Error : null);
            Assert.False(Directory.Exists(worktree));
            Assert.True(attempts >= 2);
        }
        finally
        {
            DysonGitInfo.BeforeStaleFolderDelete = null;
            _ = await DysonGitInfo.TryRemoveWorktreeAsync(repo, worktree, force: true);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task DeleteMetaAgent_clears_a_merged_stale_worktree_and_refuses_an_unmerged_branch()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            using var http = new HttpClient();
            var meta = new StubSession(DysonAgentModes.MetaAgent);
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(meta, repo, http);

            var unmergedId = Guid.NewGuid();
            var unmerged = await DysonSessionWorktree.EnsureAsync(repo, unmergedId);
            Assert.True(unmerged.IsSuccess, unmerged.IsError ? unmerged.Error : null);
            WriteAllLf(Path.Combine(unmerged.Value.AbsolutePath, "file.txt"), "ahead\n");
            RunGitOrThrow(unmerged.Value.AbsolutePath, ["add", "-A"]);
            RunGitOrThrow(unmerged.Value.AbsolutePath, ["commit", "-m", "ahead"]);

            var blockedChild = new StubSession(DysonAgentModes.MetaAgentDrone)
            {
                WorktreeAbsolutePath = unmerged.Value.AbsolutePath,
                WorktreeBranch = unmerged.Value.Branch,
            };
            Assert.True(blockedChild.TryMarkTerminal(DysonSessionStatus.Completed, "done"));
            meta.RegisterForTest(blockedChild);

            var blocked = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "1",
                ToolName = "DeleteMetaAgent",
                Stage = 0,
                ArgumentsJson = $$"""{"agentId":{{blockedChild.Id}},"reason":"prune"}""",
            });
            Assert.True(blocked.IsError);
            Assert.Contains("not in the registered checkout", blocked.Content, StringComparison.Ordinal);
            Assert.True(meta.TryGetSubagent(blockedChild.Id, out _));
            Assert.True(Directory.Exists(unmerged.Value.AbsolutePath));

            var mergedId = Guid.NewGuid();
            var mergedWt = await DysonSessionWorktree.EnsureAsync(repo, mergedId);
            Assert.True(mergedWt.IsSuccess, mergedWt.IsError ? mergedWt.Error : null);
            var mergedBranch = mergedWt.Value.Branch;
            RunGitOrThrow(repo, ["worktree", "prune"]);
            Assert.True(Directory.Exists(mergedWt.Value.AbsolutePath));

            var staleChild = new StubSession(DysonAgentModes.MetaAgentDrone)
            {
                WorktreeAbsolutePath = mergedWt.Value.AbsolutePath,
                WorktreeBranch = mergedBranch,
            };
            Assert.True(staleChild.TryMarkTerminal(DysonSessionStatus.Completed, "done"));
            meta.RegisterForTest(staleChild);

            var ok = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "2",
                ToolName = "DeleteMetaAgent",
                Stage = 0,
                ArgumentsJson = $$"""{"agentId":{{staleChild.Id}},"reason":"stale merged"}""",
            });
            Assert.False(ok.IsError, ok.Content);
            Assert.False(meta.TryGetSubagent(staleChild.Id, out _));
            Assert.False(Directory.Exists(mergedWt.Value.AbsolutePath));
            Assert.Null(staleChild.WorktreeAbsolutePath);
            Assert.Null(staleChild.WorktreeBranch);

            var refs = RunGit(repo, ["show-ref", "--verify", "--quiet", "refs/heads/" + mergedBranch]);
            Assert.Equal(0, refs);

            // Never committed: branch at the fork point counts as merged, but the dirty checkout is kept.
            var dirtyWt = await DysonSessionWorktree.EnsureAsync(repo, Guid.NewGuid());
            Assert.True(dirtyWt.IsSuccess, dirtyWt.IsError ? dirtyWt.Error : null);
            WriteAllLf(Path.Combine(dirtyWt.Value.AbsolutePath, "file.txt"), "uncommitted\n");
            var dirtyChild = new StubSession(DysonAgentModes.MetaAgentDrone)
            {
                WorktreeAbsolutePath = dirtyWt.Value.AbsolutePath,
                WorktreeBranch = dirtyWt.Value.Branch,
            };
            Assert.True(dirtyChild.TryMarkTerminal(DysonSessionStatus.Failed, "failed"));
            meta.RegisterForTest(dirtyChild);
            var dirty = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "2b",
                ToolName = "DeleteMetaAgent",
                Stage = 0,
                ArgumentsJson = $$"""{"agentId":{{dirtyChild.Id}},"reason":"dirty"}""",
            });
            Assert.True(dirty.IsError);
            Assert.True(meta.TryGetSubagent(dirtyChild.Id, out _));
            Assert.Equal("uncommitted\n", File.ReadAllText(Path.Combine(dirtyWt.Value.AbsolutePath, "file.txt")));

            var stopped = new StubSession(DysonAgentModes.MetaAgentDrone)
            {
                WorktreeAbsolutePath = unmerged.Value.AbsolutePath,
                WorktreeBranch = unmerged.Value.Branch,
            };
            meta.RegisterForTest(stopped);
            var discard = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "3",
                ToolName = "StopMetaAgentDrone",
                Stage = 0,
                ArgumentsJson = $$"""{"agentId":{{stopped.Id}},"discardWorktree":true}""",
            });
            Assert.False(discard.IsError, discard.Content);
            Assert.False(Directory.Exists(unmerged.Value.AbsolutePath));
            Assert.Null(stopped.WorktreeAbsolutePath);
            var branchStillThere = RunGit(repo, ["show-ref", "--verify", "--quiet", "refs/heads/" + unmerged.Value.Branch]);
            Assert.Equal(0, branchStillThere);
        }
        finally
        {
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Unknown_worktree_message_is_the_git_fatal()
    {
        Assert.True(DysonGitInfo.IsUnknownWorktreeMessage(
            "fatal: 'C:/wt' is not a working tree"));
        Assert.False(DysonGitInfo.IsUnknownWorktreeMessage("fatal: worktree contains modified or untracked files"));
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
        var root = Path.Combine(Path.GetTempPath(), "dyson-stale-wt-" + Guid.NewGuid().ToString("N"));
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
        RunGitOrThrow(root, ["init", "-b", "main"]);
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

    private static int RunGit(string workingDirectory, string[] args)
    {
        using var process = StartGit(workingDirectory, args);
        process.WaitForExit(15_000);
        return process.ExitCode;
    }

    private static void RunGitOrThrow(string workingDirectory, string[] args)
    {
        using var process = StartGit(workingDirectory, args);
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
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderrTask.Result}");
    }

    private static Process StartGit(string workingDirectory, string[] args)
    {
        var process = new Process
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

        return process;
    }
}

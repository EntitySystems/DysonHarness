using System.Diagnostics;
using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Meta Agent Drone worktree isolation + auto-merge on completed report.</summary>
public class DysonMetaAgentDroneWorktreeTests
{
    [Fact]
    public async Task Meta_agent_drone_gets_own_worktree_classic_child_inherits()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;

        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            var workDirs = DysonTempDb.WorkDirectories(accessor);
            var sessions = DysonTempDb.Sessions(accessor);
            var wd = await workDirs.CreateAsync(repo);
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            var provider = new DemoDysonAgentProvider(provider: null, slug: null);
            var created = await DemoDysonAgentSession.CreateAsync(
                sessions,
                provider,
                wd.Value,
                DysonAgentModes.MetaAgent,
                workDirectoryAbsolutePath: repo);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var meta = created.Value;
            meta.WorktreeEnabled = true;
            meta.WorktreeAbsolutePath = Path.Combine(parent, "meta-should-not-be-copied");
            meta.WorktreeBranch = "dyson/parentxx";

            var first = await meta.CreateChildAsync(DysonAgentModes.MetaAgentDrone, "drone a");
            Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
            var droneA = Assert.IsType<DemoDysonAgentSession>(meta.SubSessions[0]);
            Assert.True(droneA.WorktreeEnabled);
            Assert.False(string.IsNullOrWhiteSpace(droneA.WorktreeAbsolutePath));
            Assert.False(string.IsNullOrWhiteSpace(droneA.WorktreeBranch));
            Assert.NotEqual(meta.WorktreeAbsolutePath, droneA.WorktreeAbsolutePath);
            Assert.NotEqual(meta.WorktreeBranch, droneA.WorktreeBranch);
            Assert.Equal(DysonSessionWorktree.FormatBranch(droneA.PersistenceId), droneA.WorktreeBranch);
            Assert.True(SamePath(droneA.WorktreeAbsolutePath!, droneA.WorkDirectoryPath!));
            Assert.Contains(
                droneA.PersistenceId.ToString("N"),
                droneA.WorktreeAbsolutePath,
                StringComparison.OrdinalIgnoreCase);

            var second = await meta.CreateChildAsync(DysonAgentModes.MetaAgentDrone, "drone b");
            Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
            var droneB = Assert.IsType<DemoDysonAgentSession>(meta.SubSessions[1]);
            Assert.NotEqual(droneA.WorktreeAbsolutePath, droneB.WorktreeAbsolutePath);
            Assert.NotEqual(droneA.WorktreeBranch, droneB.WorktreeBranch);

            var nested = await droneA.CreateChildAsync(DysonAgentModes.Drone, "slice");
            Assert.True(nested.IsSuccess, nested.IsError ? nested.Error : null);
            var classic = Assert.IsType<DemoDysonAgentSession>(droneA.SubSessions[0]);
            Assert.Equal(droneA.WorktreeAbsolutePath, classic.WorktreeAbsolutePath);
            Assert.Equal(droneA.WorktreeBranch, classic.WorktreeBranch);
            Assert.True(SamePath(droneA.WorkDirectoryPath!, classic.WorkDirectoryPath!));

            var persistedA = await sessions.GetFullSessionAsync(droneA.PersistenceId);
            Assert.True(persistedA.IsSuccess, persistedA.IsError ? persistedA.Error : null);
            Assert.Equal(droneA.WorktreeAbsolutePath, persistedA.Value.Session.WorktreeAbsolutePath);
            Assert.Equal(droneA.WorktreeBranch, persistedA.Value.Session.WorktreeBranch);
        }
        finally
        {
            CleanupWorktrees(repo);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Completed_drone_report_merges_and_clears_self_and_descendants()
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

            var droneId = Guid.NewGuid();
            var ensured = DysonSessionWorktree.Ensure(repo, droneId);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            var wt = ensured.Value.AbsolutePath;
            var branch = ensured.Value.Branch;

            WriteAllLf(Path.Combine(wt, "file.txt"), "from-drone\n");
            RunGitOrThrow(wt, ["add", "-A"]);
            RunGitOrThrow(wt, ["commit", "-m", "drone"]);

            var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
            drone.SetPersistenceIdForTest(droneId);
            drone.WorktreeEnabled = true;
            drone.WorktreeAbsolutePath = wt;
            drone.WorktreeBranch = branch;
            drone.RegisteredWorkDirectoryAbsolutePath = repo;

            var explore = new StubSession(DysonAgentModes.Explore);
            explore.WorktreeEnabled = true;
            explore.WorktreeAbsolutePath = wt;
            explore.WorktreeBranch = branch;
            drone.RegisterForTest(explore);

            var submitted = await drone.SubmitSubagentReportAsync("done");
            Assert.True(submitted.IsSuccess, submitted.IsError ? submitted.Error : null);
            Assert.Equal(DysonSessionStatus.Completed, drone.Status);
            Assert.Contains($"Worktree {branch} merged.", drone.LastReportSummary, StringComparison.Ordinal);
            Assert.Null(drone.WorktreeAbsolutePath);
            Assert.Null(drone.WorktreeBranch);
            Assert.False(drone.WorktreeEnabled);
            Assert.Null(explore.WorktreeAbsolutePath);
            Assert.Null(explore.WorktreeBranch);
            Assert.False(explore.WorktreeEnabled);
            Assert.Equal("from-drone\n", File.ReadAllText(Path.Combine(repo, "file.txt")));

            var listed = DysonGitInfo.TryListWorktrees(repo);
            Assert.True(listed.IsSuccess, listed.IsError ? listed.Error : null);
            Assert.DoesNotContain(listed.Value, e => SamePath(e.Path, wt));
        }
        finally
        {
            CleanupWorktrees(repo);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Conflicting_merge_flips_status_to_failed_and_leaves_worktree()
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

            var droneId = Guid.NewGuid();
            var ensured = DysonSessionWorktree.Ensure(repo, droneId);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            var wt = ensured.Value.AbsolutePath;
            var branch = ensured.Value.Branch;

            WriteAllLf(Path.Combine(wt, "file.txt"), "from-drone\n");
            RunGitOrThrow(wt, ["add", "-A"]);
            RunGitOrThrow(wt, ["commit", "-m", "drone"]);

            WriteAllLf(Path.Combine(repo, "file.txt"), "from-main\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "main"]);

            var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
            drone.SetPersistenceIdForTest(droneId);
            drone.WorktreeEnabled = true;
            drone.WorktreeAbsolutePath = wt;
            drone.WorktreeBranch = branch;
            drone.RegisteredWorkDirectoryAbsolutePath = repo;

            var submitted = await drone.SubmitSubagentReportAsync("done");
            Assert.True(submitted.IsSuccess, submitted.IsError ? submitted.Error : null);
            Assert.Equal(DysonSessionStatus.Failed, drone.Status);
            Assert.Contains("Worktree merge failed", drone.LastReportSummary, StringComparison.Ordinal);
            Assert.Contains(wt, drone.LastReportSummary, StringComparison.Ordinal);
            Assert.Equal(wt, drone.WorktreeAbsolutePath);
            Assert.Equal(branch, drone.WorktreeBranch);
            Assert.True(drone.WorktreeEnabled);

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

            CleanupWorktrees(repo);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Failed_drone_report_does_not_merge()
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

            var droneId = Guid.NewGuid();
            var ensured = DysonSessionWorktree.Ensure(repo, droneId);
            Assert.True(ensured.IsSuccess, ensured.IsError ? ensured.Error : null);
            var wt = ensured.Value.AbsolutePath;
            var branch = ensured.Value.Branch;

            WriteAllLf(Path.Combine(wt, "file.txt"), "from-drone\n");
            RunGitOrThrow(wt, ["add", "-A"]);
            RunGitOrThrow(wt, ["commit", "-m", "drone"]);

            var drone = new StubSession(DysonAgentModes.MetaAgentDrone);
            drone.SetPersistenceIdForTest(droneId);
            drone.WorktreeEnabled = true;
            drone.WorktreeAbsolutePath = wt;
            drone.WorktreeBranch = branch;
            drone.RegisteredWorkDirectoryAbsolutePath = repo;

            var submitted = await drone.SubmitSubagentReportAsync("blocked", failed: true);
            Assert.True(submitted.IsSuccess, submitted.IsError ? submitted.Error : null);
            Assert.Equal(DysonSessionStatus.Failed, drone.Status);
            Assert.DoesNotContain("Worktree", drone.LastReportSummary ?? "", StringComparison.Ordinal);
            Assert.Equal(wt, drone.WorktreeAbsolutePath);
            Assert.Equal(branch, drone.WorktreeBranch);

            var listed = DysonGitInfo.TryListWorktrees(repo);
            Assert.True(listed.IsSuccess, listed.IsError ? listed.Error : null);
            Assert.Contains(listed.Value, e => SamePath(e.Path, wt));
            Assert.Equal("base\n", File.ReadAllText(Path.Combine(repo, "file.txt")));
        }
        finally
        {
            CleanupWorktrees(repo);
            DeleteQuiet(parent);
        }
    }

    [Fact]
    public async Task Failed_spawn_after_worktree_persist_drops_row_and_worktree()
    {
        var parent = CreateTempDir();
        var repo = Path.Combine(parent, "repo");
        Directory.CreateDirectory(repo);
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var keepAlive = conn;

        try
        {
            GitInit(repo);
            WriteAllLf(Path.Combine(repo, "file.txt"), "base\n");
            RunGitOrThrow(repo, ["add", "-A"]);
            RunGitOrThrow(repo, ["commit", "-m", "init"]);

            var workDirs = DysonTempDb.WorkDirectories(accessor);
            var sessions = DysonTempDb.Sessions(accessor);
            var wd = await workDirs.CreateAsync(repo);
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            var created = await DemoDysonAgentSession.CreateAsync(
                sessions,
                new DemoDysonAgentProvider(provider: null, slug: null),
                wd.Value,
                DysonAgentModes.MetaAgent,
                workDirectoryAbsolutePath: repo);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var meta = created.Value;

            var spawned = await meta.CreateChildAsync(
                DysonAgentModes.MetaAgentDrone,
                "duplicate todos",
                initialTodos:
                [
                    new DysonSessionTodoReplaceItem { TaskCode = "same", DisplayName = "A" },
                    new DysonSessionTodoReplaceItem { TaskCode = "same", DisplayName = "B" },
                ]);
            Assert.True(spawned.IsError);
            Assert.Contains("Duplicate TaskCode", spawned.Error, StringComparison.Ordinal);
            Assert.Empty(meta.SubSessions);

            var children = await sessions.ListChildSessionsAsync(meta.PersistenceId);
            Assert.True(children.IsSuccess, children.IsError ? children.Error : null);
            Assert.Empty(children.Value);

            var listed = DysonGitInfo.TryListWorktrees(repo);
            Assert.True(listed.IsSuccess, listed.IsError ? listed.Error : null);
            Assert.DoesNotContain(listed.Value, e => !SamePath(e.Path, repo));
        }
        finally
        {
            CleanupWorktrees(repo);
            DeleteQuiet(parent);
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        public void SetPersistenceIdForTest(Guid persistenceId) => SetPersistenceId(persistenceId);

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
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<DysonAgentSessionEvent, string>.AsError("not used"));
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
        var root = Path.Combine(Path.GetTempPath(), "dyson-meta-drone-wt-" + Guid.NewGuid().ToString("N"));
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

    private static void CleanupWorktrees(string repo)
    {
        if (!Directory.Exists(repo))
            return;

        var listed = DysonGitInfo.TryListWorktrees(repo);
        if (listed.IsError)
            return;

        foreach (var entry in listed.Value)
        {
            if (SamePath(entry.Path, repo))
                continue;
            _ = DysonGitInfo.TryRemoveWorktree(repo, entry.Path, force: true);
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

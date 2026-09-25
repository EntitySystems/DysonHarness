using System.Collections.Concurrent;

namespace DysonHarness;

/// <summary>Resolved per-session git worktree checkout.</summary>
public sealed record DysonSessionWorktreeLocation(string AbsolutePath, string Branch);

/// <summary>
/// Forks, copies untracked harness files, merges, and removes a session worktree.
/// Persistence and host rebind are the caller's job.
/// </summary>
public static class DysonSessionWorktree
{
    public const string NotAGitRepositoryMessage =
        "Worktree is enabled but this work directory is not a git repository.";

    /// <summary>Branch name <c>dyson/{first 8 hex of sessionId:N}</c>.</summary>
    public static string FormatBranch(Guid sessionId) =>
        $"dyson/{sessionId.ToString("N")[..8]}";

    /// <summary>
    /// Sibling of the repo: <c>{parent}/{repoName}.dyson-worktrees/{sessionId:N}</c>.
    /// Git refuses a worktree inside the main tree.
    /// </summary>
    public static Result<string, string> ResolveWorktreeAbsolutePath(string repoRoot, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            return Result<string, string>.AsError("Path is empty.");

        string fullRepo;
        try
        {
            fullRepo = Path.GetFullPath(repoRoot.Trim());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }

        var parent = Path.GetDirectoryName(fullRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var repoName = Path.GetFileName(fullRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(repoName))
        {
            return Result<string, string>.AsError(
                "Cannot resolve a sibling worktree path (repository has no parent directory).");
        }

        var path = Path.Combine(parent, repoName + ".dyson-worktrees", sessionId.ToString("N"));
        try
        {
            return Result<string, string>.AsValue(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the session worktree if missing (idempotent when already listed).
    /// Copies untracked harness files from the registered checkout when dest is missing.
    /// </summary>
    public static async Task<Result<DysonSessionWorktreeLocation, string>> EnsureAsync(
        string registeredWorkDirectoryAbsolutePath,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(registeredWorkDirectoryAbsolutePath))
            return Result<DysonSessionWorktreeLocation, string>.AsError("Path is empty.");

        var repo = await DysonGitInfo.TryFindRootMostRepoAsync(
                registeredWorkDirectoryAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (repo.IsError)
            return Result<DysonSessionWorktreeLocation, string>.AsError(NotAGitRepositoryMessage);

        var resolved = ResolveWorktreeAbsolutePath(repo.Value, sessionId);
        if (resolved.IsError)
            return Result<DysonSessionWorktreeLocation, string>.AsError(resolved.Error);

        var worktreePath = resolved.Value;
        var branch = FormatBranch(sessionId);
        var location = new DysonSessionWorktreeLocation(worktreePath, branch);

        var listed = await DysonGitInfo.TryListWorktreesAsync(repo.Value, cancellationToken)
            .ConfigureAwait(false);
        if (listed.IsError)
            return Result<DysonSessionWorktreeLocation, string>.AsError(listed.Error);

        var existing = listed.Value.FirstOrDefault(e => SamePath(e.Path, worktreePath));
        if (existing is not null)
        {
            CopyUntrackedHarnessFiles(registeredWorkDirectoryAbsolutePath, worktreePath);
            return Result<DysonSessionWorktreeLocation, string>.AsValue(
                new DysonSessionWorktreeLocation(
                    worktreePath,
                    string.IsNullOrWhiteSpace(existing.Branch) ? branch : existing.Branch));
        }

        if (Directory.Exists(worktreePath) || File.Exists(worktreePath))
        {
            return Result<DysonSessionWorktreeLocation, string>.AsError(
                "Worktree destination already exists but is not a registered git worktree.");
        }

        var added = await DysonGitInfo.TryAddWorktreeAsync(repo.Value, worktreePath, branch, cancellationToken)
            .ConfigureAwait(false);
        if (added.IsError)
            return Result<DysonSessionWorktreeLocation, string>.AsError(added.Error);

        CopyUntrackedHarnessFiles(registeredWorkDirectoryAbsolutePath, worktreePath);
        return Result<DysonSessionWorktreeLocation, string>.AsValue(location);
    }

    /// <summary>
    /// Removes the worktree checkout. Leaves the <c>dyson/…</c> branch. Does not persist.
    /// </summary>
    public static async Task<VoidResult<string>> RemoveAsync(
        string registeredWorkDirectoryAbsolutePath,
        string worktreeAbsolutePath,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var repo = await DysonGitInfo.TryFindRootMostRepoAsync(
                registeredWorkDirectoryAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (repo.IsError)
            return VoidResult<string>.AsError(repo.Error);

        return await DysonGitInfo.TryRemoveWorktreeAsync(
                repo.Value, worktreeAbsolutePath, force, cancellationToken)
            .ConfigureAwait(false);
    }

    // ponytail: one lock per repo anchor serializes concurrent drone merges; fine for tens of drones.
    // Upgrade to a per-branch queue if hundreds of drones finish together and wait on each other.
    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> MergeGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long <see cref="MergeAsync"/> waits to enter <see cref="MergeGates"/>.
    /// One in-flight merge holds the gate across merge, unmerged-list, abort, and remove
    /// (each git command is separately capped at 30s). 90s bounds that wait; it is not a sum of every call.
    /// </summary>
    internal static readonly TimeSpan MergeGateWaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Prefix on a content-conflict error after <c>git merge --abort</c>.</summary>
    public const string MergeConflictAbortedPrefix = "Merge conflict aborted:";

    /// <summary>
    /// Merges <paramref name="branchName"/> into the registered checkout, then removes the worktree.
    /// Merge conflicts leave the worktree in place. <paramref name="abortConflict"/> defaults false
    /// (UI merge keeps conflict markers). When true and unmerged paths exist, aborts that merge
    /// before releasing the gate and returns <see cref="MergeConflictAbortedPrefix"/> plus those paths.
    /// Waiting on <see cref="MergeGates"/> is bounded by <see cref="MergeGateWaitTimeout"/>.
    /// </summary>
    public static async Task<VoidResult<string>> MergeAsync(
        string registeredWorkDirectoryAbsolutePath,
        string worktreeAbsolutePath,
        string branchName,
        bool forceRemoveIfDirty = false,
        bool abortConflict = false,
        CancellationToken cancellationToken = default)
    {
        var repo = await DysonGitInfo.TryFindRootMostRepoAsync(
                registeredWorkDirectoryAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (repo.IsError)
            return VoidResult<string>.AsError(repo.Error);

        var gate = MergeGates.GetOrAdd(repo.Value, static _ => new SemaphoreSlim(1, 1));
        var entered = false;
        try
        {
            entered = await gate.WaitAsync(MergeGateWaitTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return VoidResult<string>.AsError("Worktree merge cancelled.");
        }

        if (!entered)
            return VoidResult<string>.AsError("Worktree merge timed out waiting for another merge.");

        try
        {
            var merge = await DysonGitInfo.TryMergeBranchAsync(repo.Value, branchName, cancellationToken)
                .ConfigureAwait(false);
            if (merge.IsError)
            {
                return abortConflict
                    ? await AbortContentConflictAsync(repo.Value, merge.Error, cancellationToken)
                        .ConfigureAwait(false)
                    : merge;
            }

            return await DysonGitInfo.TryRemoveWorktreeAsync(
                    repo.Value, worktreeAbsolutePath, forceRemoveIfDirty, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<VoidResult<string>> AbortContentConflictAsync(
        string repoRoot,
        string mergeError,
        CancellationToken cancellationToken)
    {
        var unmerged = await DysonGitInfo.TryListUnmergedPathsAsync(repoRoot, cancellationToken)
            .ConfigureAwait(false);
        if (unmerged.IsError || unmerged.Value.Count == 0)
            return VoidResult<string>.AsError(mergeError);

        var abort = await DysonGitInfo.TryAbortMergeAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        if (abort.IsError)
            return VoidResult<string>.AsError(mergeError + "\n" + abort.Error);

        return VoidResult<string>.AsError(
            MergeConflictAbortedPrefix + "\n" + string.Join('\n', unmerged.Value));
    }

    internal static void CopyUntrackedHarnessFiles(string sourceRoot, string destRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(destRoot))
            return;

        if (!Directory.Exists(destRoot))
            return;

        string source;
        string dest;
        try
        {
            source = Path.GetFullPath(sourceRoot.Trim());
            dest = Path.GetFullPath(destRoot.Trim());
        }
        catch
        {
            return;
        }

        CopyMissingFile(Path.Combine(source, "openrules.json"), Path.Combine(dest, "openrules.json"));
        CopyMissingFile(Path.Combine(source, "AGENTS.md"), Path.Combine(dest, "AGENTS.md"));
        CopyMissingDirectory(Path.Combine(source, ".dyson", "mcp"), Path.Combine(dest, ".dyson", "mcp"));
        CopyMissingDirectory(Path.Combine(source, ".dyson", "skills"), Path.Combine(dest, ".dyson", "skills"));
    }

    private static void CopyMissingFile(string source, string dest)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(dest))
                return;

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.Copy(source, dest);
        }
        catch
        {
            // Best-effort copy of untracked harness files; worktree itself already exists.
        }
    }

    private static void CopyMissingDirectory(string source, string dest)
    {
        try
        {
            if (!Directory.Exists(source))
                return;

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                CopyMissingFile(file, Path.Combine(dest, relative));
            }
        }
        catch
        {
            // Best-effort directory copy.
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
        }
        catch
        {
            return false;
        }
    }
}

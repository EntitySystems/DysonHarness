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
    public static Result<DysonSessionWorktreeLocation, string> Ensure(
        string registeredWorkDirectoryAbsolutePath,
        Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(registeredWorkDirectoryAbsolutePath))
            return Result<DysonSessionWorktreeLocation, string>.AsError("Path is empty.");

        var repo = DysonGitInfo.TryFindRootMostRepo(registeredWorkDirectoryAbsolutePath);
        if (repo.IsError)
            return Result<DysonSessionWorktreeLocation, string>.AsError(NotAGitRepositoryMessage);

        var resolved = ResolveWorktreeAbsolutePath(repo.Value, sessionId);
        if (resolved.IsError)
            return Result<DysonSessionWorktreeLocation, string>.AsError(resolved.Error);

        var worktreePath = resolved.Value;
        var branch = FormatBranch(sessionId);
        var location = new DysonSessionWorktreeLocation(worktreePath, branch);

        var listed = DysonGitInfo.TryListWorktrees(repo.Value);
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

        var added = DysonGitInfo.TryAddWorktree(repo.Value, worktreePath, branch);
        if (added.IsError)
            return Result<DysonSessionWorktreeLocation, string>.AsError(added.Error);

        CopyUntrackedHarnessFiles(registeredWorkDirectoryAbsolutePath, worktreePath);
        return Result<DysonSessionWorktreeLocation, string>.AsValue(location);
    }

    /// <summary>
    /// Removes the worktree checkout. Leaves the <c>dyson/…</c> branch. Does not persist.
    /// </summary>
    public static VoidResult<string> Remove(
        string registeredWorkDirectoryAbsolutePath,
        string worktreeAbsolutePath,
        bool force = false)
    {
        var repo = DysonGitInfo.TryFindRootMostRepo(registeredWorkDirectoryAbsolutePath);
        if (repo.IsError)
            return VoidResult<string>.AsError(repo.Error);

        return DysonGitInfo.TryRemoveWorktree(repo.Value, worktreeAbsolutePath, force);
    }

    /// <summary>
    /// Merges <paramref name="branchName"/> into the registered checkout, then removes the worktree.
    /// Merge conflicts leave the worktree in place.
    /// </summary>
    public static VoidResult<string> Merge(
        string registeredWorkDirectoryAbsolutePath,
        string worktreeAbsolutePath,
        string branchName,
        bool forceRemoveIfDirty = false)
    {
        var repo = DysonGitInfo.TryFindRootMostRepo(registeredWorkDirectoryAbsolutePath);
        if (repo.IsError)
            return VoidResult<string>.AsError(repo.Error);

        var merge = DysonGitInfo.TryMergeBranch(repo.Value, branchName);
        if (merge.IsError)
            return merge;

        return DysonGitInfo.TryRemoveWorktree(repo.Value, worktreeAbsolutePath, forceRemoveIfDirty);
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

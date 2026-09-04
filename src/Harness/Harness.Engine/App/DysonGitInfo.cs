using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>Git change kind from porcelain status.</summary>
public enum DysonGitChangeKind
{
    Added,
    Modified,
    Deleted,
    Untracked,
}

/// <summary>One path from <c>git status --porcelain</c>.</summary>
public sealed record DysonGitStatusEntry(string Path, DysonGitChangeKind Kind);

/// <summary>One checkout from <c>git worktree list --porcelain</c>.</summary>
public sealed record DysonGitWorktreeEntry(string Path, string Head, string? Branch);

/// <summary>
/// Reads git metadata for a workspace path (runtime, not harness build-time branch).
/// </summary>
public static class DysonGitInfo
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Mutating worktree / merge commands can take longer than status reads.</summary>
    private static readonly TimeSpan WorktreeCommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex UnifiedHunkHeader = new(
        @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Runs <c>git -C path rev-parse --abbrev-ref HEAD</c>. Failure means no usable git repo.
    /// </summary>
    public static Result<string, string> TryGetBranch(IDysonWorkspaceFileSystem workspaceFileSystem)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileSystem);
        return TryGetBranch(workspaceFileSystem.NativeRootPath);
    }

    /// <summary>
    /// Runs <c>git -C path rev-parse --abbrev-ref HEAD</c>. Failure means no usable git repo.
    /// </summary>
    public static Result<string, string> TryGetBranch(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Result<string, string>.AsError("Path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            return Result<string, string>.AsError("Directory does not exist.");

        var run = RunGit(fullPath, ["rev-parse", "--abbrev-ref", "HEAD"], Timeout);
        if (run.IsError)
            return Result<string, string>.AsError(run.Error);

        var (exitCode, stdout, stderr) = run.Value;
        var branch = stdout.Trim();
        if (exitCode != 0 || string.IsNullOrWhiteSpace(branch))
        {
            return Result<string, string>.AsError(
                string.IsNullOrWhiteSpace(stderr) ? "Not a git repository." : stderr.Trim());
        }

        return Result<string, string>.AsValue(branch);
    }

    /// <summary>
    /// Walks parents from the workspace native root and returns the outermost directory
    /// that contains a <c>.git</c> file or directory.
    /// </summary>
    public static Result<string, string> TryFindRootMostRepo(IDysonWorkspaceFileSystem workspaceFileSystem)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileSystem);
        return TryFindRootMostRepo(workspaceFileSystem.NativeRootPath);
    }

    /// <summary>
    /// Walks parents from <paramref name="absolutePath"/> and returns the outermost directory
    /// that contains a <c>.git</c> file or directory.
    /// </summary>
    public static Result<string, string> TryFindRootMostRepo(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Result<string, string>.AsError("Path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }

        string? current;
        try
        {
            if (Directory.Exists(fullPath))
                current = fullPath;
            else if (File.Exists(fullPath))
                current = Path.GetDirectoryName(fullPath);
            else
                return Result<string, string>.AsError("Path does not exist.");
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }

        string? found = null;
        while (current is not null)
        {
            var gitMarker = Path.Combine(current, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                found = current;

            DirectoryInfo? parent;
            try
            {
                parent = Directory.GetParent(current);
            }
            catch
            {
                break;
            }

            current = parent?.FullName;
        }

        return found is null
            ? Result<string, string>.AsError("No git repository.")
            : Result<string, string>.AsValue(found);
    }

    /// <summary>
    /// Runs <c>git remote get-url origin</c> against the outermost repo from
    /// <see cref="TryFindRootMostRepo(IDysonWorkspaceFileSystem)"/>.
    /// </summary>
    public static Result<string, string> TryGetOrigin(IDysonWorkspaceFileSystem workspaceFileSystem)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileSystem);
        return TryGetOrigin(workspaceFileSystem.NativeRootPath);
    }

    /// <summary>
    /// Runs <c>git remote get-url origin</c> against the outermost repo from
    /// <see cref="TryFindRootMostRepo(string)"/> so nested workdirs see the outer remote.
    /// </summary>
    public static Result<string, string> TryGetOrigin(string absolutePath)
    {
        var root = TryFindRootMostRepo(absolutePath);
        if (root.IsError)
            return Result<string, string>.AsError(root.Error);

        var run = RunGit(root.Value, ["remote", "get-url", "origin"], Timeout);
        if (run.IsError)
            return Result<string, string>.AsError(run.Error);

        var (exitCode, stdout, stderr) = run.Value;
        var origin = stdout.Trim();
        if (exitCode != 0 || string.IsNullOrWhiteSpace(origin))
        {
            return Result<string, string>.AsError(
                string.IsNullOrWhiteSpace(stderr) ? "No origin remote." : stderr.Trim());
        }

        return Result<string, string>.AsValue(origin);
    }

    /// <summary>
    /// Classifies a git remote URL as a known host family. Empty or unparseable values
    /// are <see cref="DysonGitProvider.None"/>.
    /// </summary>
    public static DysonGitProvider ClassifyProvider(string? origin)
    {
        var host = TryParseRemoteHost(origin);
        if (host is null)
            return DysonGitProvider.None;

        // GitHub / GitLab / Azure before Cursor so github.com stays GitHub
        // (GitHub-synced Origin copies still push to GitHub).
        if (host.Contains("github", StringComparison.OrdinalIgnoreCase))
            return DysonGitProvider.GitHub;
        if (host.Contains("gitlab", StringComparison.OrdinalIgnoreCase))
            return DysonGitProvider.GitLab;
        if (IsAzureDevOpsHost(host))
            return DysonGitProvider.AzureDevOps;
        if (IsCursorHost(host))
            return DysonGitProvider.CursorOrigin;

        return DysonGitProvider.Other;
    }

    /// <summary>
    /// Slug persisted for <paramref name="provider"/>. <see cref="DysonGitProvider.None"/> is null.
    /// </summary>
    public static string? ToStoredSlug(DysonGitProvider provider) => provider switch
    {
        DysonGitProvider.GitHub => "github",
        DysonGitProvider.GitLab => "gitlab",
        DysonGitProvider.AzureDevOps => "azure-devops",
        DysonGitProvider.CursorOrigin => "cursor-origin",
        DysonGitProvider.Other => "other",
        _ => null,
    };

    /// <summary>
    /// Inverse of <see cref="ToStoredSlug"/>. Unknown or empty slugs are
    /// <see cref="DysonGitProvider.None"/>.
    /// </summary>
    public static DysonGitProvider FromStoredSlug(string? stored) => stored?.Trim() switch
    {
        "github" => DysonGitProvider.GitHub,
        "gitlab" => DysonGitProvider.GitLab,
        "azure-devops" => DysonGitProvider.AzureDevOps,
        "cursor-origin" => DysonGitProvider.CursorOrigin,
        "other" => DysonGitProvider.Other,
        _ => DysonGitProvider.None,
    };

    /// <summary>
    /// Runs <c>git -C</c> against the workspace native root with <c>status --porcelain=v1 -uall</c>.
    /// Prefer resolving the repo root via <see cref="TryFindRootMostRepo(IDysonWorkspaceFileSystem)"/> first.
    /// </summary>
    public static Result<IReadOnlyList<DysonGitStatusEntry>, string> TryGetStatusPorcelain(
        IDysonWorkspaceFileSystem workspaceFileSystem)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileSystem);
        return TryGetStatusPorcelain(workspaceFileSystem.NativeRootPath);
    }

    /// <summary>
    /// Runs <c>git -C repoRoot status --porcelain=v1 -uall</c> and parses A/M/D/?? entries.
    /// </summary>
    public static Result<IReadOnlyList<DysonGitStatusEntry>, string> TryGetStatusPorcelain(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            return Result<IReadOnlyList<DysonGitStatusEntry>, string>.AsError("Path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(repoRoot.Trim());
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DysonGitStatusEntry>, string>.AsError($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            return Result<IReadOnlyList<DysonGitStatusEntry>, string>.AsError("Directory does not exist.");

        var run = RunGit(fullPath, ["status", "--porcelain=v1", "-uall"], Timeout);
        if (run.IsError)
            return Result<IReadOnlyList<DysonGitStatusEntry>, string>.AsError(run.Error);

        var (exitCode, stdout, stderr) = run.Value;
        if (exitCode != 0)
        {
            return Result<IReadOnlyList<DysonGitStatusEntry>, string>.AsError(
                string.IsNullOrWhiteSpace(stderr) ? "git status failed." : stderr.Trim());
        }

        return Result<IReadOnlyList<DysonGitStatusEntry>, string>.AsValue(ParsePorcelain(stdout));
    }

    /// <summary>
    /// Runs <c>git worktree add -b {branch} {path} HEAD</c> from <paramref name="repoRoot"/>.
    /// </summary>
    public static Result<string, string> TryAddWorktree(
        string repoRoot,
        string worktreeAbsolutePath,
        string branchName)
    {
        var root = TryResolveExistingDirectory(repoRoot);
        if (root.IsError)
            return Result<string, string>.AsError(root.Error);

        if (string.IsNullOrWhiteSpace(worktreeAbsolutePath))
            return Result<string, string>.AsError("Path is empty.");

        if (string.IsNullOrWhiteSpace(branchName))
            return Result<string, string>.AsError("Branch name is empty.");

        string fullWorktree;
        try
        {
            fullWorktree = Path.GetFullPath(worktreeAbsolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }

        var run = RunGit(
            root.Value,
            ["worktree", "add", "-b", branchName.Trim(), fullWorktree, "HEAD"],
            WorktreeCommandTimeout);
        if (run.IsError)
            return Result<string, string>.AsError(run.Error);

        var (exitCode, _, stderr) = run.Value;
        if (exitCode != 0)
        {
            return Result<string, string>.AsError(
                string.IsNullOrWhiteSpace(stderr) ? "git worktree add failed." : stderr.Trim());
        }

        return Result<string, string>.AsValue(fullWorktree);
    }

    /// <summary>
    /// Runs <c>git worktree remove</c> (with <c>--force</c> when <paramref name="force"/> is true).
    /// </summary>
    public static VoidResult<string> TryRemoveWorktree(
        string repoRoot,
        string worktreeAbsolutePath,
        bool force = false)
    {
        var root = TryResolveExistingDirectory(repoRoot);
        if (root.IsError)
            return VoidResult<string>.AsError(root.Error);

        if (string.IsNullOrWhiteSpace(worktreeAbsolutePath))
            return VoidResult<string>.AsError("Path is empty.");

        string fullWorktree;
        try
        {
            fullWorktree = Path.GetFullPath(worktreeAbsolutePath.Trim());
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Invalid path: {ex.Message}");
        }

        string[] args = force
            ? ["worktree", "remove", "--force", fullWorktree]
            : ["worktree", "remove", fullWorktree];

        var run = RunGit(root.Value, args, WorktreeCommandTimeout);
        if (run.IsError)
            return VoidResult<string>.AsError(run.Error);

        var (exitCode, _, stderr) = run.Value;
        if (exitCode != 0)
        {
            return VoidResult<string>.AsError(
                string.IsNullOrWhiteSpace(stderr) ? "git worktree remove failed." : stderr.Trim());
        }

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Runs <c>git worktree list --porcelain</c> and parses path, HEAD, and branch
    /// (null when detached).
    /// </summary>
    public static Result<IReadOnlyList<DysonGitWorktreeEntry>, string> TryListWorktrees(string repoRoot)
    {
        var root = TryResolveExistingDirectory(repoRoot);
        if (root.IsError)
            return Result<IReadOnlyList<DysonGitWorktreeEntry>, string>.AsError(root.Error);

        var run = RunGit(root.Value, ["worktree", "list", "--porcelain"], WorktreeCommandTimeout);
        if (run.IsError)
            return Result<IReadOnlyList<DysonGitWorktreeEntry>, string>.AsError(run.Error);

        var (exitCode, stdout, stderr) = run.Value;
        if (exitCode != 0)
        {
            return Result<IReadOnlyList<DysonGitWorktreeEntry>, string>.AsError(
                string.IsNullOrWhiteSpace(stderr) ? "git worktree list failed." : stderr.Trim());
        }

        return Result<IReadOnlyList<DysonGitWorktreeEntry>, string>.AsValue(ParseWorktreePorcelain(stdout));
    }

    /// <summary>
    /// Runs <c>git merge --no-edit {branch}</c> in <paramref name="repoRoot"/>.
    /// Conflicts and other non-zero exits are Result errors (stderr).
    /// </summary>
    public static VoidResult<string> TryMergeBranch(string repoRoot, string branchName)
    {
        var root = TryResolveExistingDirectory(repoRoot);
        if (root.IsError)
            return VoidResult<string>.AsError(root.Error);

        if (string.IsNullOrWhiteSpace(branchName))
            return VoidResult<string>.AsError("Branch name is empty.");

        var run = RunGit(root.Value, ["merge", "--no-edit", branchName.Trim()], WorktreeCommandTimeout);
        if (run.IsError)
            return VoidResult<string>.AsError(run.Error);

        var (exitCode, _, stderr) = run.Value;
        if (exitCode != 0)
        {
            return VoidResult<string>.AsError(
                string.IsNullOrWhiteSpace(stderr) ? "git merge failed." : stderr.Trim());
        }

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Returns unified-diff hunks for <paramref name="relativePath"/> versus <c>HEAD</c>
    /// (net staged plus unstaged). Git is optional: no repository, no usable git executable,
    /// or no comparable baseline yields an empty list. Invalid or sandbox-escaping paths
    /// remain Result errors. Untracked files, and newly added files in an unborn repository,
    /// are a single <see cref="DysonGitDiffAnnotationKind.Added"/> span of all logical source
    /// lines. Unchanged tracked files return an empty list.
    /// </summary>
    public static async Task<Result<IReadOnlyList<DysonGitDiffAnnotation>, string>> TryGetFileDiffAnnotationsAsync(
        IDysonWorkspaceFileSystem workspaceFileSystem,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceFileSystem);

        if (string.IsNullOrWhiteSpace(relativePath))
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsError("Path is empty.");

        var resolved = workspaceFileSystem.ResolvePath(relativePath);
        if (resolved.IsError)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsError(resolved.Error);

        var repo = TryFindRootMostRepo(workspaceFileSystem);
        if (repo.IsError)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        var repoRelative = TryGetRepoRelativePath(repo.Value, resolved.Value);
        if (repoRelative.IsError)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsError(repoRelative.Error);

        if (Directory.Exists(resolved.Value))
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        var statusRun = RunGit(
            repo.Value,
            ["status", "--porcelain=v1", "-uall", "--", repoRelative.Value],
            Timeout);
        if (statusRun.IsError)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        var (statusExit, statusStdout, _) = statusRun.Value;
        if (statusExit != 0)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        var statusEntries = ParsePorcelain(statusStdout);
        var isUntracked = statusEntries.Any(static e => e.Kind == DysonGitChangeKind.Untracked);
        var isNewlyAdded = statusEntries.Any(static e => e.Kind == DysonGitChangeKind.Added);
        var hasHead = HasComparableHead(repo.Value);

        if (isUntracked || (!hasHead && isNewlyAdded))
        {
            return await TryCreateFullFileAddedAnnotationAsync(
                    workspaceFileSystem, relativePath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!hasHead)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        var diffRun = RunGit(
            repo.Value,
            ["diff", "--no-color", "--no-ext-diff", "--unified=0", "HEAD", "--", repoRelative.Value],
            Timeout);
        if (diffRun.IsError)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        var (diffExit, diffStdout, _) = diffRun.Value;
        if (diffExit != 0)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue(
            ParseUnifiedDiffHunks(diffStdout));
    }

    /// <summary>Parse porcelain v1 lines into grouped change entries (public for unit tests).</summary>
    public static IReadOnlyList<DysonGitStatusEntry> ParsePorcelain(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return [];

        var entries = new List<DysonGitStatusEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Length < 3)
                continue;

            var x = rawLine[0];
            var y = rawLine[1];
            var pathPart = rawLine[2..].TrimStart();
            if (pathPart.Length == 0)
                continue;

            var path = ExtractPath(pathPart);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            path = path.Replace('\\', '/');

            var kind = Classify(x, y);
            if (kind is null)
                continue;

            if (!seen.Add(path))
                continue;

            entries.Add(new DysonGitStatusEntry(path, kind.Value));
        }

        entries.Sort(static (a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0
                ? byKind
                : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        return entries;
    }

    /// <summary>Parse unified hunk headers (omitted counts are one). Internal for unit tests.</summary>
    internal static IReadOnlyList<DysonGitDiffAnnotation> ParseUnifiedDiffHunks(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return [];

        var annotations = new List<DysonGitDiffAnnotation>();
        foreach (var rawLine in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            var match = UnifiedHunkHeader.Match(rawLine);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups[1].Value, out var originalStart))
                continue;

            var originalCount = 1;
            if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out originalCount))
                continue;

            if (!int.TryParse(match.Groups[3].Value, out var modifiedStart))
                continue;

            var modifiedCount = 1;
            if (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, out modifiedCount))
                continue;

            DysonGitDiffAnnotationKind kind;
            if (originalCount == 0 && modifiedCount > 0)
                kind = DysonGitDiffAnnotationKind.Added;
            else if (originalCount > 0 && modifiedCount == 0)
                kind = DysonGitDiffAnnotationKind.Deleted;
            else if (originalCount > 0 && modifiedCount > 0)
                kind = DysonGitDiffAnnotationKind.Modified;
            else
                continue;

            annotations.Add(new DysonGitDiffAnnotation(
                kind,
                originalStart,
                originalCount,
                modifiedStart,
                modifiedCount));
        }

        return annotations;
    }

    private static async Task<Result<IReadOnlyList<DysonGitDiffAnnotation>, string>> TryCreateFullFileAddedAnnotationAsync(
        IDysonWorkspaceFileSystem workspaceFileSystem,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var exists = await workspaceFileSystem.FileExistsAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (exists.IsError)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsError(exists.Error);

        if (!exists.Value)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        var text = await workspaceFileSystem.ReadAllTextAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (text.IsError)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsError(text.Error);

        var lineCount = CountLogicalLines(text.Value);
        if (lineCount == 0)
            return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue([]);

        return Result<IReadOnlyList<DysonGitDiffAnnotation>, string>.AsValue(
        [
            new DysonGitDiffAnnotation(
                DysonGitDiffAnnotationKind.Added,
                OriginalStartLine: 0,
                OriginalLineCount: 0,
                ModifiedStartLine: 1,
                ModifiedLineCount: lineCount),
        ]);
    }

    private static int CountLogicalLines(string text)
    {
        if (text.Length == 0)
            return 0;

        var count = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is not null)
            count++;

        return count;
    }

    private static bool HasComparableHead(string repoRoot)
    {
        var run = RunGit(repoRoot, ["rev-parse", "--verify", "HEAD"], Timeout);
        if (run.IsError)
            return false;

        return run.Value.ExitCode == 0;
    }

    private static Result<string, string> TryGetRepoRelativePath(string repoRoot, string absoluteTarget)
    {
        try
        {
            var fullRepo = Path.GetFullPath(repoRoot);
            var fullTarget = Path.GetFullPath(absoluteTarget);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var repoPrefix = fullRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            var repoTrimmed = fullRepo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetTrimmed = fullTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.Equals(targetTrimmed, repoTrimmed, comparison)
                && !fullTarget.StartsWith(repoPrefix, comparison))
            {
                return Result<string, string>.AsError("Path is outside the git repository.");
            }

            var relative = Path.GetRelativePath(fullRepo, fullTarget).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal))
                return Result<string, string>.AsError("Path is outside the git repository.");

            return Result<string, string>.AsValue(relative);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }
    }

    private static Result<string, string> TryResolveExistingDirectory(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Result<string, string>.AsError("Path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            return Result<string, string>.AsError("Directory does not exist.");

        return Result<string, string>.AsValue(fullPath);
    }

    /// <summary>Parse <c>git worktree list --porcelain</c>. Internal for unit tests.</summary>
    internal static IReadOnlyList<DysonGitWorktreeEntry> ParseWorktreePorcelain(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return [];

        var entries = new List<DysonGitWorktreeEntry>();
        string? path = null;
        string? head = null;
        string? branch = null;
        var detached = false;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            entries.Add(new DysonGitWorktreeEntry(path, head ?? "", detached ? null : branch));
            path = null;
            head = null;
            branch = null;
            detached = false;
        }

        foreach (var rawLine in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
                continue;
            }

            if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
                continue;
            }

            if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var refName = line["branch ".Length..];
                const string heads = "refs/heads/";
                branch = refName.StartsWith(heads, StringComparison.Ordinal)
                    ? refName[heads.Length..]
                    : refName;
                continue;
            }

            if (line.Equals("detached", StringComparison.Ordinal))
            {
                detached = true;
                branch = null;
            }
        }

        Flush();
        return entries;
    }

    /// <summary>
    /// Starts git with redirected pipes and drains stdout/stderr while waiting so large
    /// porcelain output cannot fill the OS pipe buffer and deadlock.
    /// </summary>
    private static Result<(int ExitCode, string Stdout, string Stderr), string> RunGit(
        string workingDirectory,
        IEnumerable<string> args,
        TimeSpan timeout)
    {
        try
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
                return Result<(int, string, string), string>.AsError("Failed to start git.");

            // Drain concurrently with WaitForExit — reading after exit deadlocks when output > pipe buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort kill on timeout.
                }

                try
                {
                    Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
                }
                catch
                {
                    // Drain best-effort after kill.
                }

                return Result<(int, string, string), string>.AsError("git timed out.");
            }

            Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
            return Result<(int, string, string), string>.AsValue(
                (process.ExitCode, stdoutTask.Result, stderrTask.Result));
        }
        catch (Exception ex)
        {
            return Result<(int, string, string), string>.AsError($"git failed: {ex.Message}");
        }
    }

    private static string? TryParseRemoteHost(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return null;

        var value = origin.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return null;

            return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
        }

        // scp-like: git@host:path
        var at = value.IndexOf('@');
        if (at <= 0)
            return null;

        var colon = value.IndexOf(':', at + 1);
        if (colon <= at + 1)
            return null;

        var host = value[(at + 1)..colon];
        if (host.Length == 0 || host.Contains('/') || host.Contains('\\'))
            return null;

        return host;
    }

    private static bool IsAzureDevOpsHost(string host) =>
        host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("visualstudio.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsCursorHost(string host) =>
        host.Equals("cursor.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".cursor.com", StringComparison.OrdinalIgnoreCase);

    private static string ExtractPath(string pathPart)
    {
        // Rename/copy: "old -> new" (optionally quoted). Prefer the destination path.
        const string arrow = " -> ";
        var arrowIdx = pathPart.IndexOf(arrow, StringComparison.Ordinal);
        var candidate = arrowIdx >= 0 ? pathPart[(arrowIdx + arrow.Length)..] : pathPart;
        return Unquote(candidate.Trim());
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return value;
    }

    private static DysonGitChangeKind? Classify(char x, char y)
    {
        if (x == '?' && y == '?')
            return DysonGitChangeKind.Untracked;

        // Ignored / blank lines
        if (x == '!' || y == '!')
            return null;

        if (IsAdded(x) || IsAdded(y))
            return DysonGitChangeKind.Added;

        if (IsDeleted(x) || IsDeleted(y))
            return DysonGitChangeKind.Deleted;

        if (IsModified(x) || IsModified(y))
            return DysonGitChangeKind.Modified;

        return null;
    }

    private static bool IsAdded(char c) => c is 'A';

    private static bool IsDeleted(char c) => c is 'D';

    private static bool IsModified(char c) => c is 'M' or 'R' or 'C' or 'U';
}

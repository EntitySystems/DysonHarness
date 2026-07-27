using System.Diagnostics;

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

/// <summary>
/// Reads git metadata for a workspace path (runtime, not harness build-time branch).
/// </summary>
public static class DysonGitInfo
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

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

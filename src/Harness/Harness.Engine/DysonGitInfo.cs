using System.Diagnostics;

namespace DysonHarness;

/// <summary>
/// Reads the current git branch for a workspace path (runtime, not harness build-time branch).
/// </summary>
public static class DysonGitInfo
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

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

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    ArgumentList = { "-C", fullPath, "rev-parse", "--abbrev-ref", "HEAD" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
                return Result<string, string>.AsError("Failed to start git.");

            if (!process.WaitForExit(Timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort kill on timeout.
                }

                return Result<string, string>.AsError("git timed out.");
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                var stderr = process.StandardError.ReadToEnd().Trim();
                return Result<string, string>.AsError(
                    string.IsNullOrWhiteSpace(stderr) ? "Not a git repository." : stderr);
            }

            return Result<string, string>.AsValue(stdout);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"git failed: {ex.Message}");
        }
    }
}

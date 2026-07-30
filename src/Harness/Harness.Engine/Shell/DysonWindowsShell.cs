using System.Diagnostics;
using System.Text;

namespace DysonHarness;

/// <summary>Windows process runner for pwsh / powershell / cmd (path- or type-based).</summary>
public sealed class DysonWindowsShell : DysonShell
{
    private const int DefaultTimeoutMs = 120_000;

    private readonly DysonShellType? _type;
    private readonly string _executablePath;

    public DysonWindowsShell(DysonShellType type)
    {
        if (type is not (DysonShellType.Pwsh or DysonShellType.PowerShell or DysonShellType.Cmd))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Not a Windows shell type.");

        _type = type;
        _executablePath = MapArgs(type).FileName;
    }

    public DysonWindowsShell(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path is required.", nameof(executablePath));

        _executablePath = executablePath.Trim();
        _type = null;
    }

    public override DysonShellType ShellType =>
        _type ?? throw new InvalidOperationException("Path-based shell has no DysonShellType.");

    public override Task<Result<DysonShellRunResult, string>> ExecuteAsync(
        string command,
        string workingDirectory,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default) =>
        ExecuteWithPathAsync(_executablePath, command, workingDirectory, timeoutMs, cancellationToken);

    /// <summary>
    /// One-shot run using an executable path.
    /// Fixed args: <paramref name="fixedArgsOverride"/> when non-empty, else basename heuristics.
    /// </summary>
    public static async Task<Result<DysonShellRunResult, string>> ExecuteWithPathAsync(
        string executablePath,
        string command,
        string workingDirectory,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? fixedArgsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Result<DysonShellRunResult, string>.AsError("Command is empty.");

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return Result<DysonShellRunResult, string>.AsError("Working directory does not exist.");

        var mapped = ResolveFixedArgs(executablePath, fixedArgsOverride);
        if (mapped.IsError)
            return Result<DysonShellRunResult, string>.AsError(mapped.Error);

        var (fileName, fixedArgs) = mapped.Value;
        var limitMs = timeoutMs is > 0 ? timeoutMs.Value : DefaultTimeoutMs;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };

            foreach (var arg in fixedArgs)
                process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.ArgumentList.Add(command);

            if (!process.Start())
                return Result<DysonShellRunResult, string>.AsError($"Failed to start {fileName}.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(limitMs);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                var stdoutTimed = await SafeRead(stdoutTask).ConfigureAwait(false);
                var stderrTimed = await SafeRead(stderrTask).ConfigureAwait(false);
                return Result<DysonShellRunResult, string>.AsValue(new DysonShellRunResult
                {
                    ExitCode = -1,
                    Stdout = stdoutTimed,
                    Stderr = string.IsNullOrEmpty(stderrTimed)
                        ? $"Timed out after {limitMs}ms."
                        : stderrTimed,
                    TimedOut = true,
                });
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return Result<DysonShellRunResult, string>.AsValue(new DysonShellRunResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdout,
                Stderr = stderr,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<DysonShellRunResult, string>.AsError("Shell execution was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<DysonShellRunResult, string>.AsError($"Shell failed: {ex.Message}");
        }
    }

    /// <summary>
    /// FileName + fixed arg prefix before the command string.
    /// Pwsh/PowerShell: -NoProfile -NonInteractive -Command; Cmd: /d /c.
    /// </summary>
    public static (string FileName, string[] FixedArgs) MapArgs(DysonShellType type) => type switch
    {
        DysonShellType.Pwsh => ("pwsh", ["-NoProfile", "-NonInteractive", "-Command"]),
        DysonShellType.PowerShell => ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command"]),
        DysonShellType.Cmd => ("cmd.exe", ["/d", "/c"]),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a Windows shell type."),
    };

    /// <summary>
    /// Prefers <paramref name="fixedArgsOverride"/> when non-empty; otherwise basename heuristics.
    /// </summary>
    public static Result<(string FileName, string[] FixedArgs), string> ResolveFixedArgs(
        string executablePath,
        IReadOnlyList<string>? fixedArgsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return Result<(string, string[]), string>.AsError("Executable path is empty.");

        var fileName = executablePath.Trim();
        if (fixedArgsOverride is { Count: > 0 })
            return Result<(string, string[]), string>.AsValue((fileName, fixedArgsOverride.ToArray()));

        return MapFixedArgsFromExecutablePath(fileName);
    }

    /// <summary>
    /// Resolves fixed args from the executable basename (so <c>C:\…\pwsh.exe</c> still gets Pwsh flags).
    /// <paramref name="executablePath"/> is used as <c>FileName</c> unchanged.
    /// </summary>
    public static Result<(string FileName, string[] FixedArgs), string> MapFixedArgsFromExecutablePath(
        string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return Result<(string, string[]), string>.AsError("Executable path is empty.");

        var fileName = executablePath.Trim();
        // Windows paths use `\`; normalize so Linux CI Path APIs still resolve the leaf basename.
        var baseName = Path.GetFileNameWithoutExtension(
            fileName.Replace('\\', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(baseName))
            return Result<(string, string[]), string>.AsError("Executable path has no file name.");

        if (baseName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            return Result<(string, string[]), string>.AsValue(
                (fileName, ["-NoProfile", "-NonInteractive", "-Command"]));
        }

        if (baseName.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            return Result<(string, string[]), string>.AsValue((fileName, ["/d", "/c"]));

        if (baseName.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("sh", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("zsh", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("git-bash", StringComparison.OrdinalIgnoreCase))
        {
            return Result<(string, string[]), string>.AsValue((fileName, ["-c"]));
        }

        return Result<(string, string[]), string>.AsError(
            $"Unsupported shell executable basename '{baseName}'. " +
            "Expected pwsh, powershell, cmd, bash, sh, zsh, or git-bash — " +
            "or set Fixed args in Settings → Shells.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort kill on timeout/cancel.
        }
    }

    private static async Task<string> SafeRead(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }
}

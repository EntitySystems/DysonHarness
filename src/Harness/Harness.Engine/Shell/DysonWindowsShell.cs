using System.Diagnostics;
using System.Text;

namespace DysonHarness;

/// <summary>Windows process runner for <see cref="DysonShellType.Pwsh"/>, PowerShell, and Cmd.</summary>
public sealed class DysonWindowsShell : DysonShell
{
    private const int DefaultTimeoutMs = 120_000;

    private readonly DysonShellType _type;

    public DysonWindowsShell(DysonShellType type)
    {
        if (type is not (DysonShellType.Pwsh or DysonShellType.PowerShell or DysonShellType.Cmd))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Not a Windows shell type.");

        _type = type;
    }

    public override DysonShellType ShellType => _type;

    public override async Task<Result<DysonShellRunResult, string>> ExecuteAsync(
        string command,
        string workingDirectory,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Result<DysonShellRunResult, string>.AsError("Command is empty.");

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return Result<DysonShellRunResult, string>.AsError("Working directory does not exist.");

        var (fileName, fixedArgs) = MapArgs(_type);
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

    /// <summary>Small self-check that the Windows arg map stays as documented.</summary>
    public static VoidResult<string> SelfCheckArgMap()
    {
        var pwsh = MapArgs(DysonShellType.Pwsh);
        if (pwsh.FileName != "pwsh"
            || pwsh.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            return new VoidResult<string>("Pwsh arg map mismatch.");
        }

        var ps = MapArgs(DysonShellType.PowerShell);
        if (ps.FileName != "powershell.exe"
            || ps.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            return new VoidResult<string>("PowerShell arg map mismatch.");
        }

        var cmd = MapArgs(DysonShellType.Cmd);
        if (cmd.FileName != "cmd.exe" || cmd.FixedArgs is not ["/d", "/c"])
            return new VoidResult<string>("Cmd arg map mismatch.");

        return VoidResult<string>.Success;
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

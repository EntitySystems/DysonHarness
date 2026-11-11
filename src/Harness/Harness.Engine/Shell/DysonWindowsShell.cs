using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DysonHarness;

/// <summary>Windows process runner for pwsh / powershell / cmd (path- or type-based).</summary>
public sealed class DysonWindowsShell : DysonShell
{
    private const int DefaultTimeoutMs = 120_000;

    /// <summary>How long pipes may stay open after the shell exits before the run is promoted (or returned as-is).</summary>
    private const int HeldPipeGraceMs = 1_000;

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
    /// With <paramref name="promotion"/>, a timeout or pipes still held after exit adopt the run into
    /// <see cref="DysonLongRunningShellRegistry"/> (<see cref="DysonShellRunResult.PromotedShellId"/>) instead of
    /// killing it or cutting its output off. User cancel always kills.
    /// </summary>
    public static async Task<Result<DysonShellRunResult, string>> ExecuteWithPathAsync(
        string executablePath,
        string command,
        string workingDirectory,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? fixedArgsOverride = null,
        DysonShellPromotion? promotion = null)
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

        Process? process = null;
        nint job = 0;
        CancellationTokenSource? timeoutCts = null;
        // Readers outlive the deadline when the run is promoted, so they get their own token.
        var readersCts = new CancellationTokenSource();
        var promoted = false;
        try
        {
            process = new Process
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

            job = WindowsJob.TryAssign(process);

            // One deadline covers the exit wait and both pipe reads.
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(limitMs);
            var stdoutCapture = new PipeCapture(DysonToolResultLimits.MaxShellStreamBytes);
            var stderrCapture = new PipeCapture(DysonToolResultLimits.MaxShellStreamBytes);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, stdoutCapture, readersCts.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, stderrCapture, readersCts.Token);

            Result<DysonShellRunResult, string>? Promote(bool timedOut)
            {
                if (promotion is null || cancellationToken.IsCancellationRequested)
                    return null;

                BoundedRead? stdoutSeed = null;
                BoundedRead? stderrSeed = null;
                var adopted = DysonLongRunningShellRegistry.Adopt(
                    promotion,
                    command,
                    workingDirectory,
                    process!,
                    job,
                    Task.WhenAll(stdoutTask, stderrTask),
                    shell =>
                    {
                        stdoutSeed = stdoutCapture.Redirect(shell, isStderr: false);
                        stderrSeed = stderrCapture.Redirect(shell, isStderr: true);
                    });
                if (adopted.IsError)
                    return null;

                promoted = true;
                // The readers outlive this call; release their token once both pipes close.
                _ = Task.WhenAll(stdoutTask, stderrTask).ContinueWith(
                    _ => readersCts.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return Result<DysonShellRunResult, string>.AsValue(new DysonShellRunResult
                {
                    ExitCode = timedOut ? -1 : process!.ExitCode,
                    Stdout = stdoutSeed!.Text,
                    Stderr = stderrSeed!.Text,
                    TimedOut = timedOut,
                    StdoutTruncated = stdoutSeed.Truncated,
                    StderrTruncated = stderrSeed.Truncated,
                    PromotedShellId = adopted.Value,
                });
            }

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (Promote(timedOut: true) is { } promotedRun)
                    return promotedRun;

                Terminate(process, job);
                await DysonCliProcess.ObservePipesAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                var stdoutTimed = stdoutCapture.Snapshot();
                var stderrTimed = stderrCapture.Snapshot();
                return Result<DysonShellRunResult, string>.AsValue(new DysonShellRunResult
                {
                    ExitCode = -1,
                    Stdout = stdoutTimed.Text,
                    Stderr = string.IsNullOrEmpty(stderrTimed.Text)
                        ? $"Timed out after {limitMs}ms."
                        : stderrTimed.Text,
                    TimedOut = true,
                    StdoutTruncated = stdoutTimed.Truncated,
                    StderrTruncated = !string.IsNullOrEmpty(stderrTimed.Text) && stderrTimed.Truncated,
                });
            }

            // A detached descendant (Start-Process, `&`, a server) inherits the pipe write ends, so EOF may
            // never come. Give the pipes a short grace, then promote (or return what was captured); not an error.
            var drained = await DysonCliProcess
                .ObservePipesAsync(stdoutTask, stderrTask, HeldPipeGraceMs)
                .ConfigureAwait(false);
            if (!drained && Promote(timedOut: false) is { } heldRun)
                return heldRun;

            var stdout = stdoutCapture.Snapshot();
            var stderr = stderrCapture.Snapshot();
            return Result<DysonShellRunResult, string>.AsValue(new DysonShellRunResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdout.Text,
                Stderr = stderr.Text,
                StdoutTruncated = stdout.Truncated,
                StderrTruncated = stderr.Truncated,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Terminate(process, job);
            return Result<DysonShellRunResult, string>.AsError("Shell execution was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<DysonShellRunResult, string>.AsError($"Shell failed: {ex.Message}");
        }
        finally
        {
            timeoutCts?.Dispose();
            // A promoted run's process, job and readers now belong to its long-running shell.
            if (!promoted)
            {
                // Stop readers still blocked on a held pipe. The job is closed without terminating it, so a
                // server the script deliberately detached survives a normal exit.
                // ponytail: a sync pipe read the runtime cannot cancel stays parked on a pool thread until the
                // holder exits; upgrade is overlapped pipes we create ourselves.
                try
                {
                    readersCts.Cancel();
                }
                catch
                {
                    // Reader cancellation is best-effort.
                }

                readersCts.Dispose();
                if (process is not null)
                {
                    TryKill(process);
                    process.Dispose();
                }

                WindowsJob.Close(job);
            }
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

        if (baseName.Equals("python", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("python3", StringComparison.OrdinalIgnoreCase))
        {
            return Result<(string, string[]), string>.AsValue((fileName, ["-c"]));
        }

        if (baseName.Equals("node", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("nodejs", StringComparison.OrdinalIgnoreCase))
        {
            return Result<(string, string[]), string>.AsValue((fileName, ["-e"]));
        }

        return Result<(string, string[]), string>.AsError(
            $"Unsupported shell executable basename '{baseName}'. " +
            "Expected pwsh, powershell, cmd, bash, sh, zsh, git-bash, python, python3, node, or nodejs — " +
            "or set Fixed args in Settings → Shells.");
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort kill on timeout/cancel.
        }
    }

    /// <summary>Timeout/cancel: kill the job (catches orphans the PID tree walk misses), then the tree.</summary>
    private static void Terminate(Process? process, nint job)
    {
        WindowsJob.Terminate(job);
        TryKill(process);
    }

    /// <summary>
    /// Drain into <paramref name="capture"/> until EOF, so the child can exit; overflow is dropped, not killed.
    /// Ends quietly when abandoned (cancel/timeout); the capture keeps what arrived.
    /// </summary>
    private static async Task ReadBoundedAsync(Stream stream, PipeCapture capture, CancellationToken cancellationToken)
    {
        var buffer = new byte[4_096];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return;
                capture.Append(buffer, read);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Pipe abandoned after grace, timeout or cancel.
        }
    }

    /// <summary>
    /// First <c>limit</c> bytes of a pipe, readable while the reader is still running. After
    /// <see cref="Redirect"/> every later chunk goes to a long-running shell's rings instead.
    /// </summary>
    private sealed class PipeCapture(int limit)
    {
        private readonly MemoryStream _captured = new(Math.Min(limit, 16 * 1024));
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private long _total;
        private DysonLongRunningShell? _target;
        private bool _targetIsStderr;

        public void Append(byte[] buffer, int read)
        {
            lock (_captured)
            {
                if (_target is not null)
                {
                    Forward(buffer, read);
                    return;
                }

                var remaining = limit - (int)_captured.Length;
                if (remaining > 0)
                    _captured.Write(buffer, 0, Math.Min(read, remaining));
                _total += read;
            }
        }

        /// <summary>Seed <paramref name="shell"/> with what was captured and forward later output; returns the seed.</summary>
        public BoundedRead Redirect(DysonLongRunningShell shell, bool isStderr)
        {
            lock (_captured)
            {
                _target = shell;
                _targetIsStderr = isStderr;
                Forward(_captured.GetBuffer(), (int)_captured.Length);
                return new BoundedRead(
                    Encoding.UTF8.GetString(_captured.GetBuffer(), 0, (int)_captured.Length),
                    _total > limit);
            }
        }

        private void Forward(byte[] buffer, int read)
        {
            if (read <= 0)
                return;
            var chars = new char[_decoder.GetCharCount(buffer, 0, read)];
            var n = _decoder.GetChars(buffer, 0, read, chars, 0);
            _target!.AppendOutput(chars.AsSpan(0, n), _targetIsStderr);
        }

        public BoundedRead Snapshot()
        {
            lock (_captured)
                return new BoundedRead(
                    Encoding.UTF8.GetString(_captured.GetBuffer(), 0, (int)_captured.Length),
                    _total > limit);
        }
    }

    private sealed record BoundedRead(string Text, bool Truncated);

    /// <summary>
    /// Windows Job Object holding the shell and every descendant, including orphans whose parent already exited.
    /// No KILL_ON_JOB_CLOSE: a normal exit closes the handle and leaves deliberately detached servers running;
    /// only timeout and cancel call TerminateJobObject.
    /// </summary>
    /// <remarks>
    /// ponytail: assigned right after Process.Start, so a grandchild spawned in that first instant escapes the job
    /// (shells take far longer to start). Upgrade is CREATE_SUSPENDED via CreateProcess.
    /// </remarks>
    internal static class WindowsJob
    {
        public static nint TryAssign(Process process)
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            try
            {
                var job = CreateJobObjectW(0, null);
                if (job == 0)
                    return 0;
                if (AssignProcessToJobObject(job, process.Handle))
                    return job;
                CloseHandle(job);
            }
            catch
            {
                // Containment is best-effort; the PID tree kill still runs.
            }

            return 0;
        }

        public static void Terminate(nint job)
        {
            if (job != 0)
                TerminateJobObject(job, 1);
        }

        public static void Close(nint job)
        {
            if (job != 0)
                CloseHandle(job);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint CreateJobObjectW(nint jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(nint job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}

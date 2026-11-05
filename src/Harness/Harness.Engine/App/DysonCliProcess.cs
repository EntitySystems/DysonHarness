using System.Diagnostics;

namespace DysonHarness;

/// <summary>Exit code and drained pipes from one CLI process.</summary>
internal readonly record struct DysonCliOutput(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Starts a CLI process with redirected pipes and drains stdout/stderr concurrently with exit
/// so large output cannot fill the OS pipe buffer and deadlock.
/// </summary>
internal static class DysonCliProcess
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="args"/>.
    /// <paramref name="timeout"/> covers the process and both pipes.
    /// A null environment value removes that variable.
    /// </summary>
    /// <remarks>
    /// ponytail: Process.Start goes through here. Tests fake it with the delegate seams on callers
    /// (e.g. the GitHub provider), not a process-runner interface.
    /// </remarks>
    internal static async Task<Result<DysonCliOutput, string>> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan timeout,
        string label,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Result<DysonCliOutput, string>.AsError($"{label} cancelled.");

        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (workingDirectory is not null)
                process.StartInfo.WorkingDirectory = workingDirectory;

            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    if (value is null)
                        process.StartInfo.Environment.Remove(key);
                    else
                        process.StartInfo.Environment[key] = value;
                }
            }

            if (!process.Start())
                return Result<DysonCliOutput, string>.AsError($"{label} failed: process did not start.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var token = timeoutCts.Token;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            string stdout;
            string stderr;
            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                stdout = await stdoutTask.ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort kill on timeout.
                }

                // ponytail: 250ms post-kill drain. stderr may still be empty if the pipe outlives the cap.
                // Upgrade is a cancellable Process wrapper if kill does not reap.
                await ObservePipesAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return Result<DysonCliOutput, string>.AsError(
                    cancellationToken.IsCancellationRequested ? $"{label} cancelled." : $"{label} timed out.");
            }

            return Result<DysonCliOutput, string>.AsValue(new DysonCliOutput(process.ExitCode, stdout, stderr));
        }
        catch (Exception ex)
        {
            return Result<DysonCliOutput, string>.AsError($"{label} failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task ObservePipesAsync(Task stdout, Task stderr)
    {
        try
        {
            var pending = Task.WhenAll(stdout, stderr);
            var finished = await Task.WhenAny(pending, Task.Delay(250)).ConfigureAwait(false);
            if (ReferenceEquals(finished, pending))
                await pending.ConfigureAwait(false);
        }
        catch
        {
            // Drain best-effort after kill.
        }
    }
}

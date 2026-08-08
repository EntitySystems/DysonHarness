using System.Diagnostics;
using System.Text;

namespace DysonHarness;

/// <summary>Direct process implementation for already-resolved, reviewed hook commands. It never invokes a shell.</summary>
public sealed class DysonPluginHookProcessRunner : IDysonPluginHookProcessRunner
{
    public async Task<Result<DysonPluginHookProcessResult, string>> RunAsync(
        DysonPluginHookProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FileName) || !Path.IsPathFullyQualified(request.WorkingDirectory))
            return Result<DysonPluginHookProcessResult, string>.AsError("Plugin hook process request is invalid.");
        if (request.TimeoutMilliseconds <= 0 || request.MaxStdoutBytes <= 0 || request.MaxStderrBytes <= 0)
            return Result<DysonPluginHookProcessResult, string>.AsError("Plugin hook process bounds are invalid.");

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var pair in request.Environment)
            startInfo.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                return Result<DysonPluginHookProcessResult, string>.AsError("Plugin hook process could not be started.");

            var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, request.MaxStdoutBytes, capture: true, overflow);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, request.MaxStderrBytes, capture: false, overflow);
            var inputTask = WriteInputAsync(process, request.StandardInput);
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var timeoutTask = Task.Delay(request.TimeoutMilliseconds, cancellationToken);
            var completed = await Task.WhenAny(exitTask, overflow.Task, timeoutTask).ConfigureAwait(false);

            var timedOut = completed == timeoutTask && !cancellationToken.IsCancellationRequested;
            if (completed != exitTask)
                TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                await AwaitAfterKillAsync(exitTask, stdoutTask, stderrTask, inputTask).ConfigureAwait(false);
                return Result<DysonPluginHookProcessResult, string>.AsError("Plugin hook execution was cancelled.");
            }

            await AwaitAfterKillAsync(exitTask, stdoutTask, stderrTask, inputTask).ConfigureAwait(false);
            stopwatch.Stop();
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return Result<DysonPluginHookProcessResult, string>.AsValue(new DysonPluginHookProcessResult
            {
                ExitCode = process.HasExited ? process.ExitCode : -1,
                StandardOutput = stdout.Text,
                StandardOutputBytes = stdout.ByteCount,
                StandardErrorBytes = stderr.ByteCount,
                DurationMilliseconds = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                TimedOut = timedOut,
                StandardOutputLimitExceeded = stdout.LimitExceeded,
                StandardErrorLimitExceeded = stderr.LimitExceeded,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TryKill(process);
            return Result<DysonPluginHookProcessResult, string>.AsError("Plugin hook process execution failed.", ex);
        }
    }

    private static async Task WriteInputAsync(Process process, string input)
    {
        try
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The bounded runner may terminate the process before it consumes stdin.
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task<BoundedRead> ReadBoundedAsync(
        Stream stream,
        int limit,
        bool capture,
        TaskCompletionSource overflow)
    {
        var buffer = new byte[4_096];
        using var captured = capture ? new MemoryStream(Math.Min(limit, 16 * 1024)) : null;
        var total = 0L;
        var exceeded = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            var remaining = Math.Max(0, limit - (int)Math.Min(total - read, limit));
            if (capture && remaining > 0)
                captured!.Write(buffer, 0, Math.Min(read, remaining));
            if (total > limit && !exceeded)
            {
                exceeded = true;
                overflow.TrySetResult();
            }
        }

        var text = capture ? Encoding.UTF8.GetString(captured!.ToArray()) : "";
        return new BoundedRead(text, (int)Math.Min(total, int.MaxValue), exceeded);
    }

    private static async Task AwaitAfterKillAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Expected when the process is terminated for timeout or output overflow.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed record BoundedRead(string Text, int ByteCount, bool LimitExceeded);
}

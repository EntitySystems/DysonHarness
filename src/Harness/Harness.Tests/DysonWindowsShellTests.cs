using System.Diagnostics;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Windows shell arg map stays as documented (Xunit).</summary>
public class DysonWindowsShellTests
{
    [Fact]
    public void ArgMap_MatchesDocumentedValues()
    {
        var pwsh = DysonWindowsShell.MapArgs(DysonShellType.Pwsh);
        if (pwsh.FileName != "pwsh"
            || pwsh.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("Pwsh arg map mismatch.");
        }

        var ps = DysonWindowsShell.MapArgs(DysonShellType.PowerShell);
        if (ps.FileName != "powershell.exe"
            || ps.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("PowerShell arg map mismatch.");
        }

        var cmd = DysonWindowsShell.MapArgs(DysonShellType.Cmd);
        if (cmd.FileName != "cmd.exe" || cmd.FixedArgs is not ["/d", "/c"])
            throw new InvalidOperationException("Cmd arg map mismatch.");
    }

    [Fact]
    public void BasenameHeuristics_UseExecutablePathAsFileName()
    {
        var customPwsh = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Tools\pwsh.exe");
        if (customPwsh.IsError
            || customPwsh.Value.FileName != @"C:\Tools\pwsh.exe"
            || customPwsh.Value.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("Basename heuristics must keep full path and Pwsh fixed args.");
        }

        var bash = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Program Files\Git\bin\bash.exe");
        if (bash.IsError
            || bash.Value.FileName != @"C:\Program Files\Git\bin\bash.exe"
            || bash.Value.FixedArgs is not ["-c"])
        {
            throw new InvalidOperationException("bash basename must map to -c.");
        }

        var gitBash = DysonWindowsShell.MapFixedArgsFromExecutablePath("git-bash.exe");
        if (gitBash.IsError || gitBash.Value.FixedArgs is not ["-c"])
            throw new InvalidOperationException("git-bash basename must map to -c.");

        var python = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Python311\python.exe");
        if (python.IsError
            || python.Value.FileName != @"C:\Python311\python.exe"
            || python.Value.FixedArgs is not ["-c"])
        {
            throw new InvalidOperationException("python basename must keep full path and map to -c.");
        }

        var python3 = DysonWindowsShell.MapFixedArgsFromExecutablePath("python3");
        if (python3.IsError || python3.Value.FixedArgs is not ["-c"])
            throw new InvalidOperationException("python3 basename must map to -c.");

        var node = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Program Files\nodejs\node.exe");
        if (node.IsError
            || node.Value.FileName != @"C:\Program Files\nodejs\node.exe"
            || node.Value.FixedArgs is not ["-e"])
        {
            throw new InvalidOperationException("node basename must keep full path and map to -e.");
        }

        var nodejs = DysonWindowsShell.MapFixedArgsFromExecutablePath("nodejs.exe");
        if (nodejs.IsError || nodejs.Value.FixedArgs is not ["-e"])
            throw new InvalidOperationException("nodejs basename must map to -e.");

        var unknown = DysonWindowsShell.MapFixedArgsFromExecutablePath("foo.exe");
        if (!unknown.IsError
            || !unknown.Error.Contains("Fixed args", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Unsupported basenames must fail and mention Settings Fixed args.");
        }
    }

    [Fact]
    public void ResolveFixedArgs_OverrideWinsOverBasename()
    {
        var overridden = DysonWindowsShell.ResolveFixedArgs("python.exe", ["-c"]);
        if (overridden.IsError
            || overridden.Value.FileName != "python.exe"
            || overridden.Value.FixedArgs is not ["-c"])
        {
            throw new InvalidOperationException("Non-empty FixedArgs override must skip basename heuristics.");
        }

        var emptyOverride = DysonWindowsShell.ResolveFixedArgs(@"C:\Tools\bash.exe", []);
        if (emptyOverride.IsError || emptyOverride.Value.FixedArgs is not ["-c"])
            throw new InvalidOperationException("Empty override must fall back to basename heuristics.");

        var nullOverride = DysonWindowsShell.ResolveFixedArgs("pwsh", null);
        if (nullOverride.IsError
            || nullOverride.Value.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("Null override must use basename heuristics.");
        }
    }

    [Fact]
    public void ExecuteWithPathAsync_UserCancel_KillsProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var cwd = Path.Combine(Path.GetTempPath(), "dyson-shell-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var pidPath = Path.Combine(cwd, "alive.pid");
        using var cts = new CancellationTokenSource();
        int? pid = null;
        try
        {
            // powershell.exe is always present on Windows; $pid is the OS process ExecuteWithPathAsync started.
            var run = DysonWindowsShell.ExecuteWithPathAsync(
                "powershell.exe",
                "$pid | Set-Content -Path 'alive.pid' -Encoding ASCII; Start-Sleep -Seconds 60",
                cwd,
                timeoutMs: 120_000,
                cts.Token);

            var readyDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < readyDeadline)
            {
                try
                {
                    if (File.Exists(pidPath)
                        && int.TryParse(File.ReadAllText(pidPath).Trim(), out var parsed)
                        && parsed > 0)
                    {
                        pid = parsed;
                        break;
                    }
                }
                catch (IOException)
                {
                    // Set-Content may still have the file open.
                }

                if (run.IsCompleted)
                {
                    var early = run.GetAwaiter().GetResult();
                    throw new InvalidOperationException(
                        "Shell exited before writing pid; cannot assert cancel-kill. " +
                        $"IsError={early.IsError} Error={early.Error}");
                }

                Thread.Sleep(50);
            }

            if (pid is null)
                throw new InvalidOperationException("Timed out waiting for shell pid file.");

            cts.Cancel();
            var result = run.GetAwaiter().GetResult();
            if (!result.IsError || result.Error != "Shell execution was cancelled.")
            {
                throw new InvalidOperationException(
                    $"Expected cancelled error, got IsError={result.IsError} Error={result.Error}.");
            }

            var goneDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < goneDeadline)
            {
                if (!IsOsProcessAlive(pid.Value))
                    return;
                Thread.Sleep(50);
            }

            throw new InvalidOperationException($"Process {pid.Value} was still alive after cancel.");
        }
        finally
        {
            if (pid is int livePid && IsOsProcessAlive(livePid))
            {
                try
                {
                    Process.GetProcessById(livePid).Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort test cleanup.
                }
            }

            try
            {
                Directory.Delete(cwd, recursive: true);
            }
            catch
            {
                // ignore cleanup races
            }
        }
    }

    [Fact]
    public async Task ExecuteWithPathAsync_StdoutOverflow_TruncatesWithoutKilling()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var cwd = Path.Combine(Path.GetTempPath(), "dyson-shell-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var result = await DysonWindowsShell.ExecuteWithPathAsync(
                "powershell.exe",
                "[Console]::Out.Write(('x' * 200000)); [Console]::Error.Write(('y' * 200000))",
                cwd,
                timeoutMs: 60_000);

            if (result.IsError)
                throw new InvalidOperationException($"Expected success, got error: {result.Error}");

            var run = result.Value;
            if (run.TimedOut)
                throw new InvalidOperationException("Overflow must drain, not time out.");
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Expected exit 0, got {run.ExitCode}. Stderr={run.Stderr}");
            }

            if (!run.StdoutTruncated)
                throw new InvalidOperationException("Expected StdoutTruncated.");
            if (!run.StderrTruncated)
                throw new InvalidOperationException("Expected StderrTruncated.");
            if (run.Stdout.Length >= 200_000)
                throw new InvalidOperationException($"Stdout length {run.Stdout.Length} was not truncated.");
            if (run.Stderr.Length >= 200_000)
                throw new InvalidOperationException($"Stderr length {run.Stderr.Length} was not truncated.");
            if (run.Stdout.Length > DysonToolResultLimits.MaxShellStreamBytes)
                throw new InvalidOperationException($"Stdout length {run.Stdout.Length} exceeded {DysonToolResultLimits.MaxShellStreamBytes} cap.");
            if (run.Stderr.Length > DysonToolResultLimits.MaxShellStreamBytes)
                throw new InvalidOperationException($"Stderr length {run.Stderr.Length} exceeded {DysonToolResultLimits.MaxShellStreamBytes} cap.");
        }
        finally
        {
            try
            {
                Directory.Delete(cwd, recursive: true);
            }
            catch
            {
                // ignore cleanup races
            }
        }
    }

    // Held-pipe repros: a background grandchild inherits the shell's stdout and outlives it.
    // Windows uses powershell.exe + Start-Process -NoNewWindow; elsewhere /bin/sh + `&`.
    private const int HolderSeconds = 30;

    private static string HeldPipeShell => OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";

    /// <summary>Command fragment that starts a pipe-holding grandchild which writes its pid to gc.pid.</summary>
    private static string StartHolder(string cwd)
    {
        if (!OperatingSystem.IsWindows())
            return $"sleep {HolderSeconds} & echo $! > gc.pid";

        File.WriteAllText(
            Path.Combine(cwd, "gc.ps1"),
            $"$pid | Set-Content -Path 'gc.pid' -Encoding ASCII; Start-Sleep -Seconds {HolderSeconds}");
        return "Start-Process powershell.exe -NoNewWindow -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','gc.ps1'";
    }

    private static string Sleep(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    private static string Echo(string text) => OperatingSystem.IsWindows() ? $"'{text}'" : $"echo {text}";

    [Fact]
    public async Task ExecuteWithPathAsync_BackgroundChildHoldsPipe_WithoutPromotion_ReturnsAfterGrace()
    {
        var cwd = NewTempDir("dyson-shell-held");
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await DysonWindowsShell.ExecuteWithPathAsync(
                HeldPipeShell,
                $"{StartHolder(cwd)}; {Echo("hello")}",
                cwd,
                timeoutMs: 120_000);
            sw.Stop();

            if (result.IsError)
                throw new InvalidOperationException($"Expected success, got error: {result.Error}");
            var run = result.Value;
            if (sw.Elapsed > TimeSpan.FromSeconds(15))
                throw new InvalidOperationException($"Held pipe blocked the call for {sw.Elapsed}.");
            if (run.TimedOut || run.ExitCode != 0)
                throw new InvalidOperationException($"Held pipe is not a failure: exit={run.ExitCode} timedOut={run.TimedOut}.");
            if (!run.Stdout.Contains("hello", StringComparison.Ordinal))
                throw new InvalidOperationException($"Captured output lost: '{run.Stdout}'.");
            if (run.PromotedShellId is not null)
                throw new InvalidOperationException("No promotion target was given; the run must not be promoted.");

            // Normal exit must not kill an intentionally detached child (job is only terminated on timeout/cancel).
            var gcPid = await WaitForPidAsync(cwd);
            if (OperatingSystem.IsWindows() && !IsOsProcessAlive(gcPid))
                throw new InvalidOperationException("Detached child was killed on normal shell exit.");
        }
        finally
        {
            CleanupHolder(cwd);
        }
    }

    [Fact]
    public async Task ExecuteWithPathAsync_BackgroundChildHoldsPipe_PromotesToLongRunningShell()
    {
        var cwd = NewTempDir("dyson-shell-held-promote");
        var workDirId = Guid.NewGuid();
        try
        {
            string holder;
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(
                    Path.Combine(cwd, "gc.ps1"),
                    "$pid | Set-Content -Path 'gc.pid' -Encoding ASCII; Start-Sleep -Seconds 3; " +
                    $"[Console]::Out.WriteLine('later-from-child'); [Console]::Out.Flush(); Start-Sleep -Seconds {HolderSeconds}");
                holder = "Start-Process powershell.exe -NoNewWindow -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','gc.ps1'";
            }
            else
            {
                holder = $"(sleep 3; echo later-from-child; sleep {HolderSeconds}) & echo $! > gc.pid";
            }

            var result = await DysonWindowsShell.ExecuteWithPathAsync(
                HeldPipeShell,
                $"{holder}; {Echo("hello")}",
                cwd,
                timeoutMs: 120_000,
                promotion: new DysonShellPromotion(workDirId, "Shell"));

            if (result.IsError)
                throw new InvalidOperationException($"Expected success, got error: {result.Error}");
            var run = result.Value;
            if (run.TimedOut || run.ExitCode != 0)
                throw new InvalidOperationException($"Held pipe is not a failure: exit={run.ExitCode} timedOut={run.TimedOut}.");
            if (run.PromotedShellId is not int id)
                throw new InvalidOperationException("Held pipe must promote to a long-running shell.");
            if (!run.Stdout.Contains("hello", StringComparison.Ordinal))
                throw new InvalidOperationException($"Captured output lost: '{run.Stdout}'.");

            var listed = DysonLongRunningShellRegistry.List(workDirId);
            if (listed.Count != 1 || listed[0].Id != id || listed[0].ShellName != "Shell")
                throw new InvalidOperationException("Promoted shell must be listed under its work directory.");

            // The tail holds the captured output plus what the background child wrote after promotion.
            var tailText = "";
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !tailText.Contains("later-from-child", StringComparison.Ordinal))
            {
                var tail = await DysonLongRunningShellRegistry.ReadTailAsync(workDirId, id, timeoutMs: 1_000);
                if (tail.IsError)
                    throw new InvalidOperationException(tail.Error);
                tailText = tail.Value.Text;
                await Task.Delay(200);
            }

            if (!tailText.Contains("hello", StringComparison.Ordinal)
                || !tailText.Contains("later-from-child", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Tail must hold seeded and later output. Got: '{tailText}'.");
            }

            var gcPid = await WaitForPidAsync(cwd);
            var abort = await DysonLongRunningShellRegistry.AbortAsync(workDirId, id);
            if (abort.IsError)
                throw new InvalidOperationException(abort.Error);
            if (!DysonLongRunningShellRegistry.TryGet(workDirId, id, out var shell)
                || shell!.Status != DysonLongRunningShellStatus.Aborted)
            {
                throw new InvalidOperationException("Abort must mark the promoted shell Aborted.");
            }

            if (OperatingSystem.IsWindows())
                await AssertGoneAsync(gcPid, "Abort must terminate the job holding the background child.");
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
            CleanupHolder(cwd);
        }
    }

    [Fact]
    public async Task ExecuteWithPathAsync_Timeout_PromotesAndKeepsProcessAlive()
    {
        var cwd = NewTempDir("dyson-shell-timeout-promote");
        var workDirId = Guid.NewGuid();
        int? pid = null;
        try
        {
            var writePid = OperatingSystem.IsWindows()
                ? "$pid | Set-Content -Path 'gc.pid' -Encoding ASCII"
                : "echo $$ > gc.pid";
            const int timeoutMs = 5_000;
            var sw = Stopwatch.StartNew();
            var result = await DysonWindowsShell.ExecuteWithPathAsync(
                HeldPipeShell,
                $"{writePid}; {Echo("started")}; {Sleep(HolderSeconds)}",
                cwd,
                timeoutMs,
                promotion: new DysonShellPromotion(workDirId, "Shell"));
            sw.Stop();

            if (result.IsError)
                throw new InvalidOperationException($"Expected promoted result, got error: {result.Error}");
            var run = result.Value;
            if (!run.TimedOut || run.PromotedShellId is not int id)
                throw new InvalidOperationException($"Expected timedOut + promoted id, got timedOut={run.TimedOut} id={run.PromotedShellId}.");
            if (sw.Elapsed > TimeSpan.FromMilliseconds(timeoutMs + 5_000))
                throw new InvalidOperationException($"Timeout promotion took {sw.Elapsed}.");
            if (!run.Stdout.Contains("started", StringComparison.Ordinal))
                throw new InvalidOperationException($"Captured output lost on timeout: '{run.Stdout}'.");

            pid = await WaitForPidAsync(cwd);
            if (!IsOsProcessAlive(pid.Value))
                throw new InvalidOperationException("A promoted timeout must not kill the process.");

            var waited = await DysonLongRunningShellRegistry.WaitForCompletionAsync(workDirId, id, timeoutMs: 500);
            if (waited.IsError || !waited.Value.Contains("\"status\":\"timeout\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"Wait on a live promoted shell must time out. Got: {waited.Value}");

            var abort = await DysonLongRunningShellRegistry.AbortAsync(workDirId, id);
            if (abort.IsError)
                throw new InvalidOperationException(abort.Error);
            await AssertGoneAsync(pid.Value, "Abort must kill the promoted process.");

            var done = await DysonLongRunningShellRegistry.WaitForCompletionAsync(workDirId, id, timeoutMs: 10_000);
            if (done.IsError || !done.Value.Contains("\"status\":\"Aborted\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"Wait after abort must be terminal. Got: {done.Value}");
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
            if (pid is int live && IsOsProcessAlive(live))
            {
                try { Process.GetProcessById(live).Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }

            CleanupHolder(cwd);
        }
    }

    [Fact]
    public async Task ExecuteWithPathAsync_TimeoutWithOrphanHoldingPipe_WithoutPromotion_KillsOrphan()
    {
        var cwd = NewTempDir("dyson-shell-timeout");
        try
        {
            // The middle process starts the holder then exits, so the holder is an orphan the PID tree walk misses.
            string orphan;
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(cwd, "mid.ps1"), StartHolder(cwd));
                orphan = "$p = Start-Process powershell.exe -NoNewWindow -PassThru -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','mid.ps1'; $p.WaitForExit()";
            }
            else
            {
                orphan = $"({StartHolder(cwd)})";
            }

            const int timeoutMs = 8_000;
            var sw = Stopwatch.StartNew();
            var result = await DysonWindowsShell.ExecuteWithPathAsync(
                HeldPipeShell,
                $"{orphan}; {Echo("started")}; {Sleep(HolderSeconds)}",
                cwd,
                timeoutMs);
            sw.Stop();

            if (result.IsError)
                throw new InvalidOperationException($"Expected timed-out result, got error: {result.Error}");
            if (!result.Value.TimedOut)
                throw new InvalidOperationException("Expected TimedOut=true.");
            if (sw.Elapsed > TimeSpan.FromMilliseconds(timeoutMs + 5_000))
                throw new InvalidOperationException($"Timeout path took {sw.Elapsed}; the drain is unbounded.");
            if (!result.Value.Stdout.Contains("started", StringComparison.Ordinal))
                throw new InvalidOperationException($"Captured output lost on timeout: '{result.Value.Stdout}'.");

            if (!OperatingSystem.IsWindows())
                return; // No job object off Windows; the orphan is reparented to init.

            var gcPid = await WaitForPidAsync(cwd);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (IsOsProcessAlive(gcPid))
            {
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException($"Orphan {gcPid} survived the timeout (job not terminated).");
                await Task.Delay(50);
            }
        }
        finally
        {
            CleanupHolder(cwd);
        }
    }

    [Fact]
    public async Task ExecuteWithPathAsync_UserCancelWithHeldPipe_ReturnsPromptly()
    {
        var cwd = NewTempDir("dyson-shell-cancel-held");
        var workDirId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        try
        {
            // A promotion target is given: user cancel must still kill and never promote.
            var run = DysonWindowsShell.ExecuteWithPathAsync(
                HeldPipeShell,
                $"{StartHolder(cwd)}; {Echo("hello")}; {Sleep(HolderSeconds)}",
                cwd,
                timeoutMs: 120_000,
                cts.Token,
                promotion: new DysonShellPromotion(workDirId, "Shell"));

            await WaitForPidAsync(cwd);
            var sw = Stopwatch.StartNew();
            cts.Cancel();
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
            sw.Stop();
            if (finished != run)
                throw new InvalidOperationException("Cancel with a held pipe did not return within 10s.");

            var result = await run;
            if (!result.IsError || result.Error != "Shell execution was cancelled.")
                throw new InvalidOperationException($"Expected cancelled error, got IsError={result.IsError} Error={result.Error}.");
            if (DysonLongRunningShellRegistry.List(workDirId).Count != 0)
                throw new InvalidOperationException("User cancel must not promote.");
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
            CleanupHolder(cwd);
        }
    }

    [Fact]
    public void ShellExecuteDescription_CarriesPromotionGuidance()
    {
        var tool = DysonMcpPipeline.CreateShellExecuteTool(["Pwsh"])
            ?? throw new InvalidOperationException("ShellExecute must exist.");
        foreach (var phrase in new[]
                 {
                     "promoted to a long-running shell", "holds stdout/stderr", "promotedToLongRunningShell id=<N>",
                     "AbortLongRunningShell", "StartLongRunningShell for servers",
                 })
        {
            if (!tool.Description.Contains(phrase, StringComparison.Ordinal))
                throw new InvalidOperationException($"ShellExecute description is missing '{phrase}'.");
        }

        if (tool.Description.Contains("killed", StringComparison.Ordinal) && !tool.Description.Contains("not killed", StringComparison.Ordinal))
            throw new InvalidOperationException("ShellExecute description must not promise a kill on timeout.");
    }

    private static async Task AssertGoneAsync(int pid, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (IsOsProcessAlive(pid))
        {
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"{message} (pid {pid} still alive)");
            await Task.Delay(50);
        }
    }

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<int> WaitForPidAsync(string cwd)
    {
        var path = Path.Combine(cwd, "gc.pid");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid) && pid > 0)
                    return pid;
            }
            catch (IOException)
            {
                // Writer may still hold the file.
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("Grandchild never wrote gc.pid.");
    }

    private static void CleanupHolder(string cwd)
    {
        try
        {
            var path = Path.Combine(cwd, "gc.pid");
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid) && IsOsProcessAlive(pid))
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }

        try
        {
            Directory.Delete(cwd, recursive: true);
        }
        catch
        {
            // ignore cleanup races
        }
    }

    private static bool IsOsProcessAlive(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

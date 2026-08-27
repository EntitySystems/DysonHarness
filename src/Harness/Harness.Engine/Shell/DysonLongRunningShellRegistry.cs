using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Workdir-keyed in-memory registry of long-running shells.
/// Shared by parent/child sessions in the same work directory. Not persisted across UI restart
/// (restart orphans OS processes; only Abort/Cancel kill them).
/// </summary>
public static class DysonLongRunningShellRegistry
{
    private static readonly ConcurrentDictionary<Guid, WorkdirBucket> Buckets = new();

    /// <summary>
    /// (workdir, shellId) → session → includeTailMaxChars for SubscribeToLongRunningShellCompletion.
    /// </summary>
    private static readonly ConcurrentDictionary<(Guid WorkDir, int ShellId), ConcurrentDictionary<DysonAgentSession, int>>
        Subscribers = new();

    /// <summary>Raised when any shell is started, updated, or stops (UI Notify / poll).</summary>
    public static event Action? Changed;

    internal static void RaiseChanged() => Changed?.Invoke();

    public static async Task<Result<DysonLongRunningShellInfo, string>> StartAsync(
        Guid workDirectoryId,
        string shellName,
        string executablePath,
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? fixedArgsOverride = null)
    {
        if (workDirectoryId == Guid.Empty)
            return Result<DysonLongRunningShellInfo, string>.AsError("Work directory id is required.");
        if (string.IsNullOrWhiteSpace(shellName))
            return Result<DysonLongRunningShellInfo, string>.AsError("Shell name is required.");
        if (string.IsNullOrWhiteSpace(executablePath))
            return Result<DysonLongRunningShellInfo, string>.AsError("Executable path is required.");
        if (string.IsNullOrWhiteSpace(command))
            return Result<DysonLongRunningShellInfo, string>.AsError("Command is empty.");
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return Result<DysonLongRunningShellInfo, string>.AsError("Working directory does not exist.");

        var mapped = DysonWindowsShell.ResolveFixedArgs(executablePath, fixedArgsOverride);
        if (mapped.IsError)
            return Result<DysonLongRunningShellInfo, string>.AsError(mapped.Error);

        cancellationToken.ThrowIfCancellationRequested();

        var bucket = Buckets.GetOrAdd(workDirectoryId, static id => new WorkdirBucket(id));
        var id = bucket.NextId();

        var (fileName, fixedArgs) = mapped.Value;
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardInput = true,
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
            {
                process.Dispose();
                return Result<DysonLongRunningShellInfo, string>.AsError($"Failed to start {fileName}.");
            }

            // Do not dispose stdin with the Process; shell owns it for Interact/Cancel.
            var stdin = process.StandardInput;
            stdin.AutoFlush = true;

            var shell = new DysonLongRunningShell
            {
                Id = id,
                WorkDirectoryId = workDirectoryId,
                ShellName = shellName.Trim(),
                Command = command,
                WorkingDirectory = workingDirectory,
                StartedUtc = DateTime.UtcNow,
            };

            var attach = shell.Attach(process, stdin);
            if (attach.IsError)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                process.Dispose();
                shell.Dispose();
                return Result<DysonLongRunningShellInfo, string>.AsError(attach.Error);
            }

            // Process lifetime owned by shell; clear local so catch doesn't dispose twice.
            process = null;
            bucket.Shells[id] = shell;
            RaiseChanged();
            return Result<DysonLongRunningShellInfo, string>.AsValue(shell.ToInfo());
        }
        catch (Exception ex)
        {
            try { process?.Kill(entireProcessTree: true); } catch { /* ignore */ }
            process?.Dispose();
            return Result<DysonLongRunningShellInfo, string>.AsError($"Failed to start long-running shell: {ex.Message}");
        }
    }

    public static bool TryGet(Guid workDirectoryId, int id, out DysonLongRunningShell? shell)
    {
        shell = null;
        if (!Buckets.TryGetValue(workDirectoryId, out var bucket))
            return false;
        return bucket.Shells.TryGetValue(id, out shell);
    }

    /// <summary>All shells for a workdir (Running + exited), newest id last.</summary>
    public static IReadOnlyList<DysonLongRunningShellInfo> List(Guid workDirectoryId)
    {
        if (!Buckets.TryGetValue(workDirectoryId, out var bucket))
            return [];

        return bucket.Shells.Values
            .Select(s => s.ToInfo())
            .OrderBy(s => s.Id)
            .ToArray();
    }

    public static int CountRunning(Guid workDirectoryId)
    {
        if (!Buckets.TryGetValue(workDirectoryId, out var bucket))
            return 0;

        var n = 0;
        foreach (var s in bucket.Shells.Values)
        {
            if (s.Status is DysonLongRunningShellStatus.Running or DysonLongRunningShellStatus.CancelRequested)
                n++;
        }

        return n;
    }

    public static async Task<Result<DysonLongRunningShellTail, string>> ReadTailAsync(
        Guid workDirectoryId,
        int id,
        int maxChars = 8 * 1024,
        long? sinceOffset = null,
        int timeoutMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryGet(workDirectoryId, id, out var shell) || shell is null)
            return Result<DysonLongRunningShellTail, string>.AsError($"Long-running shell #{id} not found.");

        return await shell.ReadTailAsync(maxChars, sinceOffset, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<VoidResult<string>> RequestCancellationAsync(
        Guid workDirectoryId,
        int id,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (!TryGet(workDirectoryId, id, out var shell) || shell is null)
            return new VoidResult<string>($"Long-running shell #{id} not found.");

        return await shell.RequestCancellationAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<VoidResult<string>> AbortAsync(
        Guid workDirectoryId,
        int id,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (!TryGet(workDirectoryId, id, out var shell) || shell is null)
            return new VoidResult<string>($"Long-running shell #{id} not found.");

        return await shell.AbortAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<VoidResult<string>> InteractAsync(
        Guid workDirectoryId,
        int id,
        string input,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        if (!TryGet(workDirectoryId, id, out var shell) || shell is null)
            return new VoidResult<string>($"Long-running shell #{id} not found.");

        return await shell.InteractAsync(input, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Register <paramref name="session"/> for a one-shot <see cref="DysonAgentInterruptKind.LongRunningShellExited"/>
    /// when the shell becomes terminal. If already terminal, fires the interrupt immediately.
    /// </summary>
    public static VoidResult<string> SubscribeToCompletion(
        Guid workDirectoryId,
        int shellId,
        DysonAgentSession session,
        int includeTailMaxChars = DysonLongRunningShellExitedFlow.DefaultIncludeTailMaxChars)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (workDirectoryId == Guid.Empty)
            return new VoidResult<string>("Work directory id is required.");
        if (!TryGet(workDirectoryId, shellId, out var shell) || shell is null)
            return new VoidResult<string>($"Long-running shell #{shellId} not found.");

        if (includeTailMaxChars <= 0)
            includeTailMaxChars = DysonLongRunningShellExitedFlow.DefaultIncludeTailMaxChars;

        if (shell.Status is DysonLongRunningShellStatus.Exited or DysonLongRunningShellStatus.Aborted)
        {
            FireShellExitedInterrupt(session, shell, wasCancelRequested: false, includeTailMaxChars);
            return VoidResult<string>.Success;
        }

        var map = Subscribers.GetOrAdd(
            (workDirectoryId, shellId),
            static _ => new ConcurrentDictionary<DysonAgentSession, int>());
        map[session] = includeTailMaxChars;
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Block until the shell is terminal or <paramref name="timeoutMs"/> elapses.
    /// Timeout is a success JSON payload (<c>status: timeout</c>), not an error.
    /// Does not enqueue ShellExited interrupts.
    /// </summary>
    public static async Task<Result<string, string>> WaitForCompletionAsync(
        Guid workDirectoryId,
        int id,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Result<string, string>.AsError("Work directory id is required.");
        if (timeoutMs <= 0)
            return Result<string, string>.AsError("timeoutMs must be greater than 0.");
        if (!TryGet(workDirectoryId, id, out var shell) || shell is null)
            return Result<string, string>.AsError($"Long-running shell #{id} not found.");

        if (shell.Status is DysonLongRunningShellStatus.Exited or DysonLongRunningShellStatus.Aborted)
            return Result<string, string>.AsValue(FormatWaitCompletionJson(shell, timedOut: false));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            try
            {
                await shell.WaitUntilTerminalAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result<string, string>.AsValue(FormatWaitCompletionJson(shell, timedOut: true));
            }

            return Result<string, string>.AsValue(FormatWaitCompletionJson(shell, timedOut: false));
        }
        catch (OperationCanceledException)
        {
            return Result<string, string>.AsError("WaitForLongRunningShellCompletion was cancelled.");
        }
    }

    private static string FormatWaitCompletionJson(DysonLongRunningShell shell, bool timedOut)
    {
        var info = shell.ToInfo();
        if (timedOut)
        {
            return JsonSerializer.Serialize(new
            {
                longRunningShellId = info.Id,
                status = "timeout",
                shellStatus = info.Status.ToString(),
                exitCode = info.ExitCode,
            });
        }

        var outcome = DysonLongRunningShellExitedFlow.MapOutcome(
            info.Status, info.ExitCode, shell.WasCancelRequested);
        return JsonSerializer.Serialize(new
        {
            longRunningShellId = info.Id,
            status = info.Status.ToString(),
            shellStatus = info.Status.ToString(),
            outcome,
            exitCode = info.ExitCode,
        });
    }

    /// <summary>Notify subscribers once when a shell becomes terminal, then clear that shell's list.</summary>
    internal static void NotifyShellTerminal(DysonLongRunningShell shell, bool wasCancelRequested)
    {
        ArgumentNullException.ThrowIfNull(shell);

        if (!Subscribers.TryRemove((shell.WorkDirectoryId, shell.Id), out var map) || map.IsEmpty)
            return;

        foreach (var (session, maxChars) in map)
            FireShellExitedInterrupt(session, shell, wasCancelRequested, maxChars);
    }

    private static void FireShellExitedInterrupt(
        DysonAgentSession session,
        DysonLongRunningShell shell,
        bool wasCancelRequested,
        int includeTailMaxChars)
    {
        var outcome = DysonLongRunningShellExitedFlow.MapOutcome(
            shell.Status, shell.ExitCode, wasCancelRequested);
        var exitText = shell.ExitCode is int code ? code.ToString() : "unknown";

        session.EnqueueInterrupt(new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.LongRunningShellExited,
            SubagentId = 0, // ponytail: required field; unused for shell interrupts
            WorkDirectoryId = shell.WorkDirectoryId,
            LongRunningShellId = shell.Id,
            ExitCode = shell.ExitCode,
            ShellOutcome = outcome,
            IncludeTailMaxChars = includeTailMaxChars > 0
                ? includeTailMaxChars
                : DysonLongRunningShellExitedFlow.DefaultIncludeTailMaxChars,
            Summary = $"Long-running shell #{shell.Id} {outcome} (exitCode={exitText})",
        });
    }

    /// <summary>Test/self-check helper: drop a workdir bucket (does not kill processes).</summary>
    internal static void ClearForTests(Guid workDirectoryId)
    {
        if (!Buckets.TryRemove(workDirectoryId, out var bucket))
            return;

        foreach (var shell in bucket.Shells.Values)
        {
            try { shell.Dispose(); } catch { /* ignore */ }
        }

        foreach (var key in Subscribers.Keys)
        {
            if (key.WorkDir == workDirectoryId)
                Subscribers.TryRemove(key, out _);
        }
    }

    private sealed class WorkdirBucket(Guid workDirectoryId)
    {
        public Guid WorkDirectoryId { get; } = workDirectoryId;
        public ConcurrentDictionary<int, DysonLongRunningShell> Shells { get; } = new();
        private int _nextId;

        public int NextId() => Interlocked.Increment(ref _nextId);
    }
}

using System.Diagnostics;
using System.Text;

namespace DysonHarness;

/// <summary>
/// In-memory handle for a background shell process with capped stdout/stderr/combined rings.
/// Does not resume across UI/process restarts (OS children are orphaned on host exit).
/// </summary>
public sealed class DysonLongRunningShell : IDisposable
{
    /// <summary>ponytail: 256KB ring ceiling per stream; raise or spill to disk if server logs grow.</summary>
    public const int RingCapChars = 256 * 1024;

    private readonly object _gate = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _combined = new();
    private long _stdoutBaseOffset;
    private long _stderrBaseOffset;
    private long _combinedBaseOffset;
    private long _stdoutEndOffset;
    private long _stderrEndOffset;
    private long _combinedEndOffset;
    private readonly SemaphoreSlim _combinedSignal = new(0, int.MaxValue);
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private int _disposed;

    public required int Id { get; init; }
    public required Guid WorkDirectoryId { get; init; }
    public required DysonShellType ShellType { get; init; }
    public required string Command { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTime StartedUtc { get; init; }

    public DysonLongRunningShellStatus Status { get; private set; } = DysonLongRunningShellStatus.Running;
    public int? ExitCode { get; private set; }

    /// <summary>Snapshot for UI/list rows (no Process handle).</summary>
    public DysonLongRunningShellInfo ToInfo()
    {
        lock (_gate)
        {
            return new DysonLongRunningShellInfo
            {
                Id = Id,
                WorkDirectoryId = WorkDirectoryId,
                ShellType = ShellType,
                Command = Command,
                WorkingDirectory = WorkingDirectory,
                StartedUtc = StartedUtc,
                Status = Status,
                ExitCode = ExitCode,
                CombinedEndOffset = _combinedEndOffset,
            };
        }
    }

    internal VoidResult<string> Attach(Process process, StreamWriter stdin)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(stdin);

        lock (_gate)
        {
            if (_process is not null)
                return new VoidResult<string>("Process already attached.");

            _process = process;
            _stdin = stdin;
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
        }

        _stdoutPump = PumpAsync(process.StandardOutput, isStderr: false);
        _stderrPump = PumpAsync(process.StandardError, isStderr: true);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Tail of the combined ring. When <paramref name="timeoutMs"/> &gt; 0, waits for new bytes
    /// past <paramref name="sinceOffset"/> (or any growth when null) up to the timeout.
    /// </summary>
    public async Task<Result<DysonLongRunningShellTail, string>> ReadTailAsync(
        int maxChars,
        long? sinceOffset = null,
        int timeoutMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (maxChars <= 0)
            maxChars = 8 * 1024;

        var waitUntil = timeoutMs > 0
            ? DateTime.UtcNow.AddMilliseconds(timeoutMs)
            : DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var startWanted = sinceOffset ?? Math.Max(0, _combinedEndOffset - maxChars);
                if (startWanted < _combinedBaseOffset)
                    startWanted = _combinedBaseOffset;

                if (_combinedEndOffset > startWanted || timeoutMs <= 0 || DateTime.UtcNow >= waitUntil
                    || Status is not (DysonLongRunningShellStatus.Running or DysonLongRunningShellStatus.CancelRequested))
                {
                    var text = SliceCombined(startWanted, maxChars);
                    return Result<DysonLongRunningShellTail, string>.AsValue(new DysonLongRunningShellTail
                    {
                        Text = text,
                        Status = Status,
                        ExitCode = ExitCode,
                        NextOffset = _combinedEndOffset,
                        BaseOffset = _combinedBaseOffset,
                    });
                }
            }

            var remaining = waitUntil - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                lock (_gate)
                {
                    var startWanted = sinceOffset ?? Math.Max(0, _combinedEndOffset - maxChars);
                    if (startWanted < _combinedBaseOffset)
                        startWanted = _combinedBaseOffset;
                    return Result<DysonLongRunningShellTail, string>.AsValue(new DysonLongRunningShellTail
                    {
                        Text = SliceCombined(startWanted, maxChars),
                        Status = Status,
                        ExitCode = ExitCode,
                        NextOffset = _combinedEndOffset,
                        BaseOffset = _combinedBaseOffset,
                    });
                }
            }

            try
            {
                await _combinedSignal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Wait timed out — loop to return current snapshot.
            }
        }
    }

    public async Task<VoidResult<string>> InteractAsync(
        string input,
        int timeoutMs = 5_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        StreamWriter? writer;
        lock (_gate)
        {
            if (Status is not (DysonLongRunningShellStatus.Running or DysonLongRunningShellStatus.CancelRequested))
                return new VoidResult<string>($"Shell #{Id} is not running (status={Status}).");
            writer = _stdin;
            if (writer is null)
                return new VoidResult<string>($"Shell #{Id} has no stdin.");
        }

        var payload = input.EndsWith('\n') || input.EndsWith("\r\n", StringComparison.Ordinal)
            ? input
            : input + "\n";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
            cts.CancelAfter(timeoutMs);

        try
        {
            await writer.WriteAsync(payload.AsMemory(), cts.Token).ConfigureAwait(false);
            await writer.FlushAsync(cts.Token).ConfigureAwait(false);
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new VoidResult<string>($"Interact timed out after {timeoutMs}ms.");
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Interact failed: {ex.Message}");
        }
    }

    public async Task<VoidResult<string>> RequestCancellationAsync(
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        Process? process;
        StreamWriter? writer;
        lock (_gate)
        {
            if (Status is DysonLongRunningShellStatus.Exited or DysonLongRunningShellStatus.Aborted)
                return VoidResult<string>.Success;

            Status = DysonLongRunningShellStatus.CancelRequested;
            process = _process;
            writer = _stdin;
        }

        DysonLongRunningShellRegistry.RaiseChanged();

        var softOk = false;
        if (writer is not null)
        {
            try
            {
                await writer.WriteAsync("\u0003".AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                softOk = true;
            }
            catch
            {
                // Fall through to CloseMainWindow.
            }
        }

        if (!softOk && process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.CloseMainWindow();
            }
            catch
            {
                // Best-effort soft cancel.
            }
        }

        return await WaitUntilNotRunningAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoidResult<string>> AbortAsync(
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_gate)
        {
            if (Status is DysonLongRunningShellStatus.Exited or DysonLongRunningShellStatus.Aborted)
                return VoidResult<string>.Success;

            process = _process;
            Status = DysonLongRunningShellStatus.Aborted;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort kill.
            }
        }

        MarkExitedIfNeeded(aborted: true);
        DysonLongRunningShellRegistry.RaiseChanged();
        return await WaitUntilNotRunningAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Process? process;
        StreamWriter? writer;
        lock (_gate)
        {
            process = _process;
            writer = _stdin;
            _stdin = null;
        }

        try { writer?.Dispose(); } catch { /* ignore */ }

        if (process is not null)
        {
            try { process.Exited -= OnProcessExited; } catch { /* ignore */ }
            try { process.Dispose(); } catch { /* ignore */ }
        }

        _combinedSignal.Dispose();
    }

    private async Task<VoidResult<string>> WaitUntilNotRunningAsync(
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var limit = timeoutMs > 0 ? timeoutMs : 10_000;
        var deadline = DateTime.UtcNow.AddMilliseconds(limit);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (Status is not (DysonLongRunningShellStatus.Running or DysonLongRunningShellStatus.CancelRequested))
                    return VoidResult<string>.Success;
                if (_process is { HasExited: true })
                {
                    MarkExitedIfNeeded(aborted: Status == DysonLongRunningShellStatus.Aborted);
                    return VoidResult<string>.Success;
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (Status is not (DysonLongRunningShellStatus.Running or DysonLongRunningShellStatus.CancelRequested))
                return VoidResult<string>.Success;
        }

        return new VoidResult<string>($"Timed out waiting for shell #{Id} to exit after {limit}ms.");
    }

    private void OnProcessExited(object? sender, EventArgs e) =>
        MarkExitedIfNeeded(aborted: false);

    private void MarkExitedIfNeeded(bool aborted)
    {
        bool becameTerminal;
        bool wasCancelRequested;
        lock (_gate)
        {
            if (Status is DysonLongRunningShellStatus.Exited
                || (Status is DysonLongRunningShellStatus.Aborted && ExitCode is not null))
            {
                return;
            }

            wasCancelRequested = Status == DysonLongRunningShellStatus.CancelRequested;

            int? code = null;
            try
            {
                if (_process is { HasExited: true })
                    code = _process.ExitCode;
            }
            catch
            {
                // ExitCode may throw if not exited.
            }

            ExitCode = code;
            if (Status != DysonLongRunningShellStatus.Aborted)
                Status = aborted ? DysonLongRunningShellStatus.Aborted : DysonLongRunningShellStatus.Exited;
            else if (aborted)
                Status = DysonLongRunningShellStatus.Aborted;

            becameTerminal = true;
        }

        try { _combinedSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ } catch (ObjectDisposedException) { /* ignore */ }
        DysonLongRunningShellRegistry.RaiseChanged();
        if (becameTerminal)
            DysonLongRunningShellRegistry.NotifyShellTerminal(this, wasCancelRequested);
    }

    private async Task PumpAsync(StreamReader reader, bool isStderr)
    {
        var buf = new char[4096];
        try
        {
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false);
                if (n <= 0)
                    break;

                Append(buf.AsSpan(0, n), isStderr);
            }
        }
        catch
        {
            // Process/stream closed.
        }
        finally
        {
            MarkExitedIfNeeded(aborted: false);
        }
    }

    private void Append(ReadOnlySpan<char> chunk, bool isStderr)
    {
        lock (_gate)
        {
            if (isStderr)
            {
                _stderr.Append(chunk);
                _stderrEndOffset += chunk.Length;
                TrimRing(_stderr, ref _stderrBaseOffset, ref _stderrEndOffset);
            }
            else
            {
                _stdout.Append(chunk);
                _stdoutEndOffset += chunk.Length;
                TrimRing(_stdout, ref _stdoutBaseOffset, ref _stdoutEndOffset);
            }

            _combined.Append(chunk);
            _combinedEndOffset += chunk.Length;
            TrimRing(_combined, ref _combinedBaseOffset, ref _combinedEndOffset);
        }

        // ponytail: no Changed on every chunk — UI polls tails at ~500ms while modal open.
        try { _combinedSignal.Release(); } catch (SemaphoreFullException) { /* ignore */ } catch (ObjectDisposedException) { /* ignore */ }
    }

    private static void TrimRing(StringBuilder sb, ref long baseOffset, ref long endOffset)
    {
        if (sb.Length <= RingCapChars)
            return;

        var drop = sb.Length - RingCapChars;
        sb.Remove(0, drop);
        baseOffset += drop;
        // endOffset unchanged (absolute).
        _ = endOffset;
    }

    private string SliceCombined(long startWanted, int maxChars)
    {
        if (_combined.Length == 0 || startWanted >= _combinedEndOffset)
            return "";

        var localStart = (int)(startWanted - _combinedBaseOffset);
        if (localStart < 0)
            localStart = 0;
        if (localStart >= _combined.Length)
            return "";

        var len = Math.Min(maxChars, _combined.Length - localStart);
        return _combined.ToString(localStart, len);
    }
}

/// <summary>UI/list snapshot of a long-running shell (no live Process).</summary>
public sealed class DysonLongRunningShellInfo
{
    public required int Id { get; init; }
    public required Guid WorkDirectoryId { get; init; }
    public required DysonShellType ShellType { get; init; }
    public required string Command { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DysonLongRunningShellStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public long CombinedEndOffset { get; init; }
}

/// <summary>Tail read result from <see cref="DysonLongRunningShell.ReadTailAsync"/>.</summary>
public sealed class DysonLongRunningShellTail
{
    public required string Text { get; init; }
    public required DysonLongRunningShellStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public long NextOffset { get; init; }
    public long BaseOffset { get; init; }
}

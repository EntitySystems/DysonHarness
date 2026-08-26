namespace Harness.UI.Demo;

/// <summary>
/// Circuit-wide leading-edge + trailing-edge notify window.
/// </summary>
/// <remarks>
/// ponytail: ceiling = one 75ms window for the whole circuit (not per turn).
/// Upgrade later only if a user action feels lagged during a heavy stream;
/// leading-edge already makes idle clicks immediate.
/// </remarks>
internal sealed class DysonUiNotifyCoalescer : IDisposable
{
    public const int WindowMs = 75;

    private readonly Action _invoke;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private long _lastNotifyTicks;
    private bool _pending;
    private bool _disposed;
    private CancellationTokenSource? _pendingCts;

    public DysonUiNotifyCoalescer(Action invoke)
        : this(invoke, static (ms, ct) => Task.Delay(ms, ct))
    {
    }

    public DysonUiNotifyCoalescer(Action invoke, Func<int, CancellationToken, Task> delay)
    {
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public void Notify()
    {
        var fireNow = false;
        CancellationTokenSource? trailingCts = null;
        var delayMs = 0;

        lock (_lock)
        {
            if (_disposed)
                return;

            var now = Environment.TickCount64;
            var elapsed = now - _lastNotifyTicks;
            if (elapsed >= WindowMs)
            {
                CancelPendingLocked();
                _lastNotifyTicks = now;
                _pending = false;
                fireNow = true;
            }
            else if (!_pending)
            {
                _pending = true;
                delayMs = Math.Max(0, (int)(WindowMs - elapsed));
                trailingCts = new CancellationTokenSource();
                _pendingCts = trailingCts;
            }
        }

        if (fireNow)
            _invoke();

        if (trailingCts is not null)
            _ = RunTrailingAsync(delayMs, trailingCts);
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            CancelPendingLocked();
            _pending = false;
            _lastNotifyTicks = Environment.TickCount64;
        }

        _invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            CancelPendingLocked();
            _pending = false;
        }
    }

    private async Task RunTrailingAsync(int delayMs, CancellationTokenSource cts)
    {
        try
        {
            await _delay(delayMs, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        var fire = false;
        lock (_lock)
        {
            if (_disposed || !_pending || !ReferenceEquals(_pendingCts, cts))
                return;

            _pending = false;
            _lastNotifyTicks = Environment.TickCount64;
            _pendingCts = null;
            fire = true;
        }

        if (fire)
            _invoke();
    }

    private void CancelPendingLocked()
    {
        var cts = _pendingCts;
        _pendingCts = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }
}

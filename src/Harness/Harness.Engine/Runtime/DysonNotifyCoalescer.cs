namespace DysonHarness;

/// <summary>
/// Leading-edge + trailing-edge notify window. Accumulates a <see cref="DysonHostChangeKind"/>
/// mask across coalesced calls and delivers it once per invoke.
/// </summary>
/// <remarks>
/// ponytail: ceiling = one 75ms window for the whole owner (not per subscriber).
/// Upgrade later only if a user action feels lagged during a heavy stream;
/// leading-edge already makes idle clicks immediate.
/// </remarks>
public sealed class DysonNotifyCoalescer : IDisposable
{
    public const int WindowMs = 75;

    private readonly Action<DysonHostChangeKind> _invoke;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private long _lastNotifyTicks;
    private bool _pending;
    private DysonHostChangeKind _pendingMask;
    private bool _disposed;
    private CancellationTokenSource? _pendingCts;

    public DysonNotifyCoalescer(Action<DysonHostChangeKind> invoke)
        : this(invoke, static (ms, ct) => Task.Delay(ms, ct))
    {
    }

    public DysonNotifyCoalescer(Action<DysonHostChangeKind> invoke, Func<int, CancellationToken, Task> delay)
    {
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public void Notify() => Notify(DysonHostChangeKind.All);

    public void Notify(DysonHostChangeKind kind)
    {
        var fireNow = false;
        var fireMask = DysonHostChangeKind.None;
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
                fireMask = _pendingMask | kind;
                _pendingMask = DysonHostChangeKind.None;
                fireNow = true;
            }
            else
            {
                _pendingMask |= kind;
                if (!_pending)
                {
                    _pending = true;
                    delayMs = Math.Max(0, (int)(WindowMs - elapsed));
                    trailingCts = new CancellationTokenSource();
                    _pendingCts = trailingCts;
                }
            }
        }

        if (fireNow)
            _invoke(fireMask);

        if (trailingCts is not null)
            _ = RunTrailingAsync(delayMs, trailingCts);
    }

    public void Flush()
    {
        DysonHostChangeKind fireMask;
        lock (_lock)
        {
            if (_disposed)
                return;

            CancelPendingLocked();
            _pending = false;
            _lastNotifyTicks = Environment.TickCount64;
            fireMask = _pendingMask;
            _pendingMask = DysonHostChangeKind.None;
        }

        _invoke(fireMask);
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
            _pendingMask = DysonHostChangeKind.None;
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
        var fireMask = DysonHostChangeKind.None;
        lock (_lock)
        {
            if (_disposed || !_pending || !ReferenceEquals(_pendingCts, cts))
                return;

            _pending = false;
            _lastNotifyTicks = Environment.TickCount64;
            _pendingCts = null;
            fireMask = _pendingMask;
            _pendingMask = DysonHostChangeKind.None;
            fire = true;
        }

        if (fire)
            _invoke(fireMask);
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

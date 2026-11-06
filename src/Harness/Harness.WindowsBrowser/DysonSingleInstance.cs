using System.IO;

namespace DysonHarness;

/// <summary>
/// Per-user, per-app-mode single instance for the Windows shell. The first process creates a named event and
/// listens on it; a later launch finds the event, signals it (the first process activates its window) and exits.
/// </summary>
public sealed class DysonSingleInstance : IDisposable
{
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle? _wait;

    private DysonSingleInstance(EventWaitHandle signal, bool isFirst, Action onActivate)
    {
        _signal = signal;
        IsFirst = isFirst;
        if (isFirst)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(
                signal, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    /// <summary>True for the process that owns the name; false when another instance was signalled instead.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// Claim <paramref name="name"/> (session-local). On a second claim the first owner's
    /// <paramref name="onActivate"/> runs on a thread-pool thread.
    /// </summary>
    public static Result<DysonSingleInstance, string> Claim(string name, Action onActivate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(onActivate);
        try
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name, out var createdNew);
            if (!createdNew)
                signal.Set();
            return Result<DysonSingleInstance, string>.AsValue(new DysonSingleInstance(signal, createdNew, onActivate));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return Result<DysonSingleInstance, string>.AsError("Single-instance claim failed: " + ex.Message, ex);
        }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _signal.Dispose();
    }
}

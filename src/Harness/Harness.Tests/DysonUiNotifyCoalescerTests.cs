using Harness.UI.Demo;

namespace Harness.Tests;

public class DysonUiNotifyCoalescerTests
{
    [Fact]
    public void Notify_fires_immediately_on_leading_edge()
    {
        var count = 0;
        using var coalescer = new DysonUiNotifyCoalescer(
            () => Interlocked.Increment(ref count),
            HangDelay);

        coalescer.Notify();
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Notify_burst_collapses_to_one_trailing()
    {
        var count = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coalescer = new DysonUiNotifyCoalescer(
            () => Interlocked.Increment(ref count),
            (_, ct) => gate.Task.WaitAsync(ct));

        coalescer.Notify();
        coalescer.Notify();
        coalescer.Notify();
        Assert.Equal(1, Volatile.Read(ref count));

        Assert.True(gate.TrySetResult());
        await WaitUntilAsync(() => Volatile.Read(ref count) == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public void Flush_invokes_immediately_and_cancels_trailing()
    {
        var count = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coalescer = new DysonUiNotifyCoalescer(
            () => Interlocked.Increment(ref count),
            (_, ct) => gate.Task.WaitAsync(ct));

        coalescer.Notify();
        coalescer.Notify();
        Assert.Equal(1, Volatile.Read(ref count));

        coalescer.Flush();
        Assert.Equal(2, Volatile.Read(ref count));

        gate.TrySetResult();
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Dispose_cancels_pending_and_does_not_fire()
    {
        var count = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coalescer = new DysonUiNotifyCoalescer(
            () => Interlocked.Increment(ref count),
            (_, ct) => gate.Task.WaitAsync(ct));

        coalescer.Notify();
        coalescer.Notify();
        Assert.Equal(1, Volatile.Read(ref count));

        coalescer.Dispose();
        gate.TrySetResult();
        await Task.Delay(30);
        Assert.Equal(1, Volatile.Read(ref count));
        coalescer.Notify();
        coalescer.Flush();
        Assert.Equal(1, Volatile.Read(ref count));
    }

    private static Task HangDelay(int _, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !predicate())
            await Task.Delay(10);

        Assert.True(predicate());
    }
}

using DysonHarness;

namespace Harness.Tests;

public class DysonNotifyCoalescerMaskTests
{
    [Fact]
    public async Task Trailing_invoke_delivers_ored_mask_and_resets_pending_mask()
    {
        var delivered = new List<DysonHostChangeKind>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coalescer = new DysonNotifyCoalescer(
            kind => delivered.Add(kind),
            (_, ct) => gate.Task.WaitAsync(ct));

        coalescer.Notify(DysonHostChangeKind.Streaming);
        coalescer.Notify(DysonHostChangeKind.Busy);
        coalescer.Notify(DysonHostChangeKind.Transcript);
        Assert.Single(delivered);
        Assert.Equal(DysonHostChangeKind.Streaming, delivered[0]);

        Assert.True(gate.TrySetResult());
        await WaitUntilAsync(() => delivered.Count == 2, TimeSpan.FromSeconds(2));

        Assert.Equal(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript, delivered[1]);

        coalescer.Flush();
        Assert.Equal(DysonHostChangeKind.None, delivered[2]);
    }

    [Fact]
    public void Flush_after_streaming_then_busy_keeps_masks_distinct_overlay_filter_matches_neither()
    {
        var delivered = new List<DysonHostChangeKind>();
        using var coalescer = new DysonNotifyCoalescer(
            kind => delivered.Add(kind),
            HangDelay);

        coalescer.Notify(DysonHostChangeKind.Streaming);
        coalescer.Notify(DysonHostChangeKind.Busy);
        Assert.Single(delivered);
        Assert.Equal(DysonHostChangeKind.Streaming, delivered[0]);

        coalescer.Flush();
        Assert.Equal(2, delivered.Count);
        Assert.Equal(DysonHostChangeKind.Busy, delivered[1]);

        const DysonHostChangeKind overlayFilter = DysonHostChangeKind.Overlay;
        Assert.Equal(DysonHostChangeKind.None, delivered[0] & overlayFilter);
        Assert.Equal(DysonHostChangeKind.None, delivered[1] & overlayFilter);
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

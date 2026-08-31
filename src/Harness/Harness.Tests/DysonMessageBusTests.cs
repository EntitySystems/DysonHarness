using DysonHarness;

namespace Harness.Tests;

public class DysonMessageBusTests
{
    [Fact]
    public void Routes_by_event_type_and_scope_key()
    {
        using var bus = new DysonMessageBus();
        var sameKeyOtherType = new List<int>();
        var sameTypeOtherKey = new List<string>();
        var exact = new List<string>();

        Assert.True(bus.Subscribe<OtherEvt>("k", e => sameKeyOtherType.Add(e.N)).IsSuccess);
        Assert.True(bus.Subscribe<TestEvt>("other", e => sameTypeOtherKey.Add(e.X)).IsSuccess);
        Assert.True(bus.Subscribe<TestEvt>("k", e => exact.Add(e.X)).IsSuccess);

        Assert.True(bus.Publish("k", new TestEvt("a")).IsSuccess);
        Assert.True(bus.Publish("k", new OtherEvt(7)).IsSuccess);
        Assert.True(bus.Publish("other", new TestEvt("b")).IsSuccess);

        Assert.Equal(["a"], exact);
        Assert.Equal([7], sameKeyOtherType);
        Assert.Equal(["b"], sameTypeOtherKey);
    }

    [Fact]
    public void Wildcard_receives_every_key_for_that_type_only()
    {
        using var bus = new DysonMessageBus();
        var received = new List<string>();
        var otherType = new List<int>();

        Assert.True(bus.Subscribe<TestEvt>(DysonBusScopes.Wildcard, e => received.Add(e.X)).IsSuccess);
        Assert.True(bus.Subscribe<OtherEvt>(DysonBusScopes.Wildcard, e => otherType.Add(e.N)).IsSuccess);

        Assert.True(bus.Publish("a", new TestEvt("one")).IsSuccess);
        Assert.True(bus.Publish("b", new TestEvt("two")).IsSuccess);
        Assert.True(bus.Publish("a", new OtherEvt(3)).IsSuccess);

        Assert.Equal(["one", "two"], received);
        Assert.Equal([3], otherType);
    }

    [Fact]
    public void Exact_key_subscriber_does_not_receive_other_keys()
    {
        using var bus = new DysonMessageBus();
        var received = new List<string>();
        Assert.True(bus.Subscribe<TestEvt>("session:1", e => received.Add(e.X)).IsSuccess);

        Assert.True(bus.Publish("session:2", new TestEvt("nope")).IsSuccess);
        Assert.True(bus.Publish(DysonBusScopes.Wildcard, new TestEvt("star")).IsSuccess);

        Assert.Empty(received);
    }

    [Fact]
    public void Disposing_subscription_stops_delivery()
    {
        using var bus = new DysonMessageBus();
        var count = 0;
        var sub = bus.Subscribe<TestEvt>("k", _ => count++).Value;

        Assert.True(bus.Publish("k", new TestEvt("a")).IsSuccess);
        sub.Dispose();
        Assert.True(bus.Publish("k", new TestEvt("b")).IsSuccess);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Double_dispose_of_subscription_is_noop()
    {
        using var bus = new DysonMessageBus();
        var sub = bus.Subscribe<TestEvt>("k", _ => { }).Value;
        sub.Dispose();
        sub.Dispose();
    }

    [Fact]
    public void Unsubscribe_during_publish_still_runs_remaining_handlers()
    {
        using var bus = new DysonMessageBus();
        var ran = new List<int>();
        IDisposable? second = null;

        Assert.True(bus.Subscribe<TestEvt>("k", _ =>
        {
            ran.Add(1);
            second!.Dispose();
        }).IsSuccess);
        second = bus.Subscribe<TestEvt>("k", _ => ran.Add(2)).Value;
        Assert.True(bus.Subscribe<TestEvt>("k", _ => ran.Add(3)).IsSuccess);

        Assert.True(bus.Publish("k", new TestEvt("x")).IsSuccess);
        Assert.Equal([1, 2, 3], ran);
    }

    [Fact]
    public void Throwing_handler_does_not_fail_publish_or_skip_later_handlers()
    {
        using var bus = new DysonMessageBus();
        var later = false;

        Assert.True(bus.Subscribe<TestEvt>("k", _ => throw new InvalidOperationException("boom")).IsSuccess);
        Assert.True(bus.Subscribe<TestEvt>("k", _ => later = true).IsSuccess);

        var result = bus.Publish("k", new TestEvt("x"));
        Assert.True(result.IsSuccess);
        Assert.True(later);
    }

    [Fact]
    public async Task PublishAsync_awaits_async_handlers()
    {
        using var bus = new DysonMessageBus();
        var completed = false;

        Assert.True(bus.Subscribe<TestEvt>("k", async (_, ct) =>
        {
            await Task.Yield();
            completed = true;
        }).IsSuccess);

        var result = await bus.PublishAsync("k", new TestEvt("x"));
        Assert.True(result.IsSuccess);
        Assert.True(completed);
    }

    [Fact]
    public async Task Publish_does_not_block_on_async_handlers()
    {
        using var bus = new DysonMessageBus();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;

        Assert.True(bus.Subscribe<TestEvt>("k", async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            completed = true;
        }).IsSuccess);

        var result = bus.Publish("k", new TestEvt("x"));
        Assert.True(result.IsSuccess);
        Assert.False(completed);

        Assert.True(gate.TrySetResult());
        await WaitUntilAsync(() => completed, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Publish_to_unknown_key_succeeds_with_zero_handlers()
    {
        using var bus = new DysonMessageBus();
        Assert.True(bus.Publish("nobody", new TestEvt("x")).IsSuccess);
    }

    [Fact]
    public void Empty_whitespace_key_and_null_handler_or_message_return_error()
    {
        using var bus = new DysonMessageBus();

        Assert.True(bus.Publish<TestEvt>("k", null!).IsError);
        Assert.True(bus.Publish(" ", new TestEvt("x")).IsError);
        Assert.True(bus.Publish("", new TestEvt("x")).IsError);
        Assert.True(bus.Subscribe<TestEvt>("", _ => { }).IsError);
        Assert.True(bus.Subscribe<TestEvt>("\t", _ => { }).IsError);
        Assert.True(bus.Subscribe<TestEvt>("k", (Action<TestEvt>)null!).IsError);
        Assert.True(bus.Subscribe<TestEvt>("k", (Func<TestEvt, CancellationToken, Task>)null!).IsError);
    }

    [Fact]
    public void Disposed_bus_rejects_publish_and_subscribe_and_tokens_dispose_safely()
    {
        var bus = new DysonMessageBus();
        var token = bus.Subscribe<TestEvt>("k", _ => { }).Value;
        bus.Dispose();

        Assert.True(bus.Publish("k", new TestEvt("x")).IsError);
        Assert.True(bus.Subscribe<TestEvt>("k", _ => { }).IsError);
        token.Dispose();
        token.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !predicate())
            await Task.Delay(10);

        Assert.True(predicate());
    }
}

file sealed record TestEvt(string X) : IDysonMessageBusEvent;

file sealed record OtherEvt(int N) : IDysonMessageBusEvent;

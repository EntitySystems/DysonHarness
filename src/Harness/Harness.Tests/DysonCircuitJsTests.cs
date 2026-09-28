using Harness.UI;
using Microsoft.JSInterop;

namespace Harness.Tests;

public class DysonCircuitJsTests
{
    [Fact]
    public void Production_cap_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DysonCircuitJs.Timeout);
    }

    [Fact]
    public async Task Benign_failures_do_not_throw()
    {
        Exception[] failures =
        [
            new TaskCanceledException(),
            new OperationCanceledException(),
            new JSDisconnectedException("gone"),
            new JSException("js"),
            new InvalidOperationException("prerender"),
            new ObjectDisposedException("js"),
        ];

        foreach (var failure in failures)
        {
            var js = new ScriptedJs(failure);
            var swallowed = await DysonCircuitJs.InvokeVoidAsync(js, "x");
            Assert.Same(failure, swallowed);

            var value = await DysonCircuitJs.InvokeAsync<string>(js, "x");
            Assert.Null(value);
        }
    }

    [Fact]
    public async Task Other_exceptions_propagate()
    {
        var js = new ScriptedJs(new InvalidDataException("boom"));

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(
            () => DysonCircuitJs.InvokeVoidAsync(js, "x").AsTask());
        Assert.Equal("boom", thrown.Message);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => DysonCircuitJs.InvokeAsync<int>(js, "x").AsTask());
    }

    [Fact(Timeout = 5000)]
    public async Task None_token_is_still_canceled_by_the_cap()
    {
        var js = new HangUntilCanceledJs();
        var value = await DysonCircuitJs.InvokeAsync<int>(
            js,
            "hang",
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.Equal(0, value);
        Assert.True(js.CanceledByCap);
    }

    [Fact]
    public async Task Navigate_uses_the_client_helper()
    {
        var js = new ScriptedJs(null);
        var failure = await DysonCircuitJs.NavigateAsync(js, "/meta/1", replace: true);

        Assert.Null(failure);
        Assert.Equal(DysonCircuitJs.NavigateFunction, js.Identifier);
        Assert.Equal("/meta/1", js.Args?[0]);
        Assert.Equal(true, js.Args?[1]);
    }

    private sealed class ScriptedJs(Exception? failure) : IJSRuntime
    {
        public string? Identifier { get; private set; }

        public object?[]? Args { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Identifier = identifier;
            Args = args;
            if (failure is not null)
                throw failure;

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class HangUntilCanceledJs : IJSRuntime
    {
        public bool CanceledByCap { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.True(cancellationToken.CanBeCanceled);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CanceledByCap = cancellationToken.IsCancellationRequested;
                throw;
            }

            return default!;
        }
    }
}

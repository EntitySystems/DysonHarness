using Microsoft.JSInterop;

namespace Harness.UI;

/// <summary>
/// Circuit JS interop with a short cap. A canceled or disconnected call must not
/// escape onto the Blazor circuit (that tears the session down and shows the yellow bar).
/// </summary>
public static class DysonCircuitJs
{
    /// <summary>Max wait for a circuit JS call, including <c>CancellationToken.None</c>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public const string NavigateFunction = "dysonNav.go";

    public static bool IsBenign(Exception exception) =>
        exception is OperationCanceledException
            or JSDisconnectedException
            or JSException
            or InvalidOperationException
            or ObjectDisposedException;

    public static ValueTask<Exception?> InvokeVoidAsync(
        IJSRuntime js,
        string identifier,
        params object?[]? args) =>
        InvokeVoidAsync(js, identifier, CancellationToken.None, args);

    public static ValueTask<Exception?> InvokeVoidAsync(
        IJSRuntime js,
        string identifier,
        CancellationToken cancellationToken,
        params object?[]? args) =>
        InvokeVoidAsync(js, identifier, Timeout, cancellationToken, args);

    /// <summary>Returns null on success, or the benign exception that was swallowed.</summary>
    public static async ValueTask<Exception?> InvokeVoidAsync(
        IJSRuntime js,
        string identifier,
        TimeSpan cap,
        CancellationToken cancellationToken,
        params object?[]? args)
    {
        using var timeout = new CancellationTokenSource(cap);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await js.InvokeVoidAsync(identifier, linked.Token, args).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            return ex;
        }
    }

    public static ValueTask<Exception?> InvokeVoidAsync(
        IJSObjectReference target,
        string identifier,
        params object?[]? args) =>
        InvokeVoidAsync(target, identifier, CancellationToken.None, args);

    public static async ValueTask<Exception?> InvokeVoidAsync(
        IJSObjectReference target,
        string identifier,
        CancellationToken cancellationToken,
        params object?[]? args)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await target.InvokeVoidAsync(identifier, linked.Token, args).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            return ex;
        }
    }

    public static ValueTask<T?> InvokeAsync<T>(
        IJSRuntime js,
        string identifier,
        params object?[]? args) =>
        InvokeAsync<T>(js, identifier, Timeout, CancellationToken.None, args);

    public static ValueTask<T?> InvokeAsync<T>(
        IJSRuntime js,
        string identifier,
        CancellationToken cancellationToken,
        params object?[]? args) =>
        InvokeAsync<T>(js, identifier, Timeout, cancellationToken, args);

    public static async ValueTask<T?> InvokeAsync<T>(
        IJSRuntime js,
        string identifier,
        TimeSpan cap,
        CancellationToken cancellationToken,
        params object?[]? args)
    {
        using var timeout = new CancellationTokenSource(cap);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await js.InvokeAsync<T>(identifier, linked.Token, args).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            return default;
        }
    }

    /// <summary>
    /// Client <c>Blazor.navigateTo</c>. Does not call <c>NavigationManager.NavigateTo</c>,
    /// whose canceled JS invoke terminates the circuit.
    /// </summary>
    public static ValueTask<Exception?> NavigateAsync(IJSRuntime js, string relativeUri, bool replace = false) =>
        InvokeVoidAsync(js, NavigateFunction, relativeUri, replace);
}

// Derived from router-for-me/CLIProxyAPI (MIT) internal/auth/codex/oauth_server.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.
// Bind is 127.0.0.1 (not all interfaces): the redirect is loopback-only.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DysonHarness;

/// <summary>What the browser posted back to the loopback listener.</summary>
public sealed record OpenAiCodexCallbackResult(bool Ok, string? Code, string? State, string? Error);

/// <summary>
/// One-shot loopback listener for the ChatGPT redirect. Binds 127.0.0.1:1455.
/// <see cref="Start"/> must be called off the Blazor circuit (the auth service uses <c>Task.Run</c>).
/// Tests inject <see cref="TryBind"/>; production uses a real socket.
/// </summary>
public class OpenAiCodexCallback : IAsyncDisposable
{
    public const string PortInUseMessage =
        "Codex login needs localhost port 1455 free (something else is using it).";

    public const string PortReservedMessage =
        "Windows has reserved localhost port 1455, so Codex login cannot listen for the browser. Connect failed.";

    private readonly Func<Result<TcpListener, string>> _tryBind;
    private readonly TaskCompletionSource<OpenAiCodexCallbackResult> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public OpenAiCodexCallback()
        : this(TryBindLoopback)
    {
    }

    /// <param name="tryBind">Returns a started listener, or an error the UI can show. Tests pass a double.</param>
    public OpenAiCodexCallback(Func<Result<TcpListener, string>> tryBind)
    {
        _tryBind = tryBind ?? throw new ArgumentNullException(nameof(tryBind));
    }

    public bool IsBound => _listener is not null;

    /// <summary>Bind and start accepting. Call from <c>Task.Run</c>, not the circuit.</summary>
    /// <summary>Test seam. Production is the real bind; tests replace start and wait without a socket.</summary>
    internal Func<Result<bool, string>>? StartOverride { get; set; }

    internal Func<TimeSpan, CancellationToken, Task<OpenAiCodexCallbackResult>>? WaitOverride { get; set; }

    public Result<bool, string> Start()
    {
        if (StartOverride is not null)
            return StartOverride();

        if (_listener is not null)
            return Result<bool, string>.AsValue(true);

        var bound = _tryBind();
        if (bound.IsError)
            return Result<bool, string>.AsError(bound.Error);

        _listener = bound.Value;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        return Result<bool, string>.AsValue(true);
    }

    public Task<OpenAiCodexCallbackResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitOverride is not null
            ? WaitOverride(timeout, cancellationToken)
            : WaitCoreAsync(timeout, cancellationToken);

    public void Cancel() => _done.TrySetResult(new OpenAiCodexCallbackResult(false, null, null, "cancelled"));

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Classify a failed bind. Access-denied is a Windows excluded-port range, not "another app".</summary>
    public static string DescribeBindFailure(SocketException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressNotAvailable
            ? PortReservedMessage
            : PortInUseMessage;
    }

    private static Result<TcpListener, string> TryBindLoopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, OpenAiCodexClientProfile.CallbackPort);
        try
        {
            listener.Start();
            return Result<TcpListener, string>.AsValue(listener);
        }
        catch (SocketException ex)
        {
            return Result<TcpListener, string>.AsError(DescribeBindFailure(ex));
        }
    }

    private async Task<OpenAiCodexCallbackResult> WaitCoreAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _done.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OpenAiCodexCallbackResult(false, null, null, "timed out");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_done.Task.IsCompleted)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Listener stopped, or the wait already finished.
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? "";
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    // Drain headers. The callback is a GET with no body.
                }

                var result = ParseRequestLine(requestLine);
                if (result.Ok)
                {
                    var okHead = "HTTP/1.1 302 Found\r\nLocation: /success\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(okHead)).ConfigureAwait(false);
                }
                else if (result.Error is not null)
                {
                    var failBody = Encoding.UTF8.GetBytes("<!doctype html><title>Sign-in failed</title><p>ChatGPT sign-in did not complete. Return to Dyson and try Connect again.</p>");
                    var failHead = $"HTTP/1.1 400 Bad Request\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {failBody.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(failHead)).ConfigureAwait(false);
                    await stream.WriteAsync(failBody).ConfigureAwait(false);
                }

                if (result.Ok || result.Error is not null)
                    _done.TrySetResult(result);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // A dropped browser connection is not the login result.
            }
        }
    }

    /// <summary><c>GET /auth/callback?code=&amp;state=</c>. <c>error</c> fails. Anything else is ignored.</summary>
    internal static OpenAiCodexCallbackResult ParseRequestLine(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            return new OpenAiCodexCallbackResult(false, null, null, null);

        var target = parts[1];
        var q = target.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? target : target[..q];
        if (!path.Equals(OpenAiCodexClientProfile.CallbackPath, StringComparison.Ordinal))
            return new OpenAiCodexCallbackResult(false, null, null, null);

        var query = q < 0 ? "" : target[(q + 1)..];
        string? code = null;
        string? state = null;
        string? error = null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            switch (key)
            {
                case "code": code = value; break;
                case "state": state = value; break;
                case "error": error = value; break;
            }
        }

        if (!string.IsNullOrEmpty(error))
            return new OpenAiCodexCallbackResult(false, null, state, error);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return new OpenAiCodexCallbackResult(false, null, state, "missing code or state");

        return new OpenAiCodexCallbackResult(true, code, state, null);
    }

    private async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
    }
}

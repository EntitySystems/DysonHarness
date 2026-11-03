// Derived from router-for-me/CLIProxyAPI (MIT) internal/auth/codex/openai_auth.go
//   (refresh single-flight) @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.

using System.Collections.Concurrent;

namespace DysonHarness;

/// <summary>What Connect shows: the authorize link and the state that completes it. No user code (not a device flow).</summary>
public sealed record OpenAiCodexLoginStart(string AuthorizeUrl, string State);

/// <summary>
/// Singleton owner of ChatGPT Codex OAuth state: one pending loopback login (in memory, lost on restart),
/// access-token resolution with per-credential single-flight refresh, and sign-out. State is keyed by
/// credential handle (<c>dyson-openai-codex:&lt;guid&gt;</c>), not by subject, because HTTP handlers have no
/// circuit scope. Persists rotated tokens BEFORE returning them. Never logs tokens.
/// The listener is started on the thread pool, never on the caller (Blazor circuit).
/// </summary>
public sealed class OpenAiCodexAuthService(
    IDysonOpenAiCodexCredentialStore store,
    OpenAiCodexOAuthClient oauth,
    TimeProvider? clock = null,
    Func<OpenAiCodexCallback>? callbackFactory = null)
{
    public const string HandlePrefix = "dyson-openai-codex:";

    internal const string SignInExpired =
        "ChatGPT sign-in expired. Use Connect on the ChatGPT Codex card.";

    internal const string AccountMissing =
        "ChatGPT account id missing; Connect again.";

    private readonly IDysonOpenAiCodexCredentialStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly OpenAiCodexOAuthClient _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<OpenAiCodexCallback> _callbackFactory = callbackFactory ?? (() => new OpenAiCodexCallback());

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly object _pendingLock = new();
    private PendingLogin? _pending;

    public static string HandleFor(Guid credentialId) => HandlePrefix + credentialId.ToString("D");

    public static bool TryParseHandle(string? value, out Guid credentialId)
    {
        credentialId = Guid.Empty;
        return value is not null
            && value.StartsWith(HandlePrefix, StringComparison.Ordinal)
            && Guid.TryParse(value.AsSpan(HandlePrefix.Length), out credentialId);
    }

    /// <summary>
    /// Bind the loopback listener (off this caller's thread) and return the authorize URL.
    /// <paramref name="credentialId"/> is where the tokens land on success.
    /// </summary>
    public async Task<Result<OpenAiCodexLoginStart, string>> BeginAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty)
            return Result<OpenAiCodexLoginStart, string>.AsError("Credential id is required.");

        await DropPendingAsync().ConfigureAwait(false);

        var callback = _callbackFactory();
        var started = await Task.Run(callback.Start, cancellationToken).ConfigureAwait(false);
        if (started.IsError)
        {
            await callback.DisposeAsync().ConfigureAwait(false);
            return Result<OpenAiCodexLoginStart, string>.AsError(started.Error);
        }

        var verifier = OpenAiCodexPkce.CreateVerifier();
        var state = OpenAiCodexPkce.CreateState();
        var url = OpenAiCodexClientProfile.BuildAuthorizeUrl(state, OpenAiCodexPkce.Challenge(verifier));
        lock (_pendingLock)
            _pending = new PendingLogin(state, verifier, credentialId, callback);

        return Result<OpenAiCodexLoginStart, string>.AsValue(new OpenAiCodexLoginStart(url, state));
    }

    /// <summary>
    /// Wait for the browser callback that <see cref="BeginAsync"/> already started, exchange the code, and
    /// save the credential. Does not poll. State must equal the state we sent.
    /// </summary>
    public async Task<Result<string, string>> CompleteAsync(
        string state,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
            return Result<string, string>.AsError("Subject id is required.");

        PendingLogin? pending;
        lock (_pendingLock)
            pending = _pending;

        if (pending is null || !string.Equals(pending.State, state?.Trim(), StringComparison.Ordinal))
            return Result<string, string>.AsError("No pending ChatGPT sign-in; start Connect again.");

        var callback = await pending.Callback
            .WaitAsync(OpenAiCodexClientProfile.CallbackWait, cancellationToken)
            .ConfigureAwait(false);
        await DropPendingAsync().ConfigureAwait(false);

        if (!callback.Ok)
            return Result<string, string>.AsError(CallbackError(callback));
        if (!string.Equals(callback.State, pending.State, StringComparison.Ordinal))
            return Result<string, string>.AsError("ChatGPT sign-in state did not match. Start Connect again.");

        var exchanged = await _oauth
            .ExchangeCodeAsync(callback.Code!, pending.Verifier, cancellationToken)
            .ConfigureAwait(false);
        if (exchanged.IsError)
            return Result<string, string>.AsError(exchanged.Error);
        if (string.IsNullOrWhiteSpace(exchanged.Value.AccountId))
            return Result<string, string>.AsError(AccountMissing);

        var credential = OpenAiCodexCredential.FromTokens(exchanged.Value, _clock.GetUtcNow());
        var saved = await _store
            .SaveAsync(pending.CredentialId, subjectId.Trim(), credential.ToJson(), CancellationToken.None)
            .ConfigureAwait(false);
        return saved.IsError
            ? Result<string, string>.AsError($"Could not save the ChatGPT sign-in: {saved.Error}")
            : Result<string, string>.AsValue(credential.Email ?? "");
    }

    /// <summary>Drop the pending listener (user pressed Cancel).</summary>
    public Task CancelAsync() => DropPendingAsync();

    /// <summary>
    /// Current access token for <paramref name="handle"/>, refreshing within
    /// <see cref="OpenAiCodexClientProfile.RefreshLead"/> of expiry. When <paramref name="rejectedAccessToken"/>
    /// is the token ChatGPT just refused, a refresh is forced unless another caller already rotated it.
    /// A credential with no account id is an error (the handler must not send without one).
    /// </summary>
    public async Task<Result<OpenAiCodexAccess, string>> GetAccessTokenAsync(
        string handle,
        string? rejectedAccessToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return Result<OpenAiCodexAccess, string>.AsError("Not a ChatGPT Codex credential handle.");

        var gate = _gates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (loaded.IsError)
                return Result<OpenAiCodexAccess, string>.AsError(loaded.Error);

            var cred = loaded.Value;
            if (string.IsNullOrWhiteSpace(cred.AccountId))
                return Result<OpenAiCodexAccess, string>.AsError(AccountMissing);

            var now = _clock.GetUtcNow();
            var rejected = !string.IsNullOrEmpty(rejectedAccessToken)
                && string.Equals(cred.AccessToken, rejectedAccessToken, StringComparison.Ordinal);
            if (!rejected && !cred.NeedsRefresh(now, OpenAiCodexClientProfile.RefreshLead))
                return Result<OpenAiCodexAccess, string>.AsValue(new OpenAiCodexAccess(cred.AccessToken, cred.AccountId));

            if (string.IsNullOrWhiteSpace(cred.RefreshToken))
                return Result<OpenAiCodexAccess, string>.AsError(SignInExpired);

            // CancellationToken.None: a dropped caller must not abandon a refresh that may rotate the token.
            var refreshed = await _oauth.RefreshAsync(cred.RefreshToken, CancellationToken.None).ConfigureAwait(false);
            if (refreshed.IsError)
                return Result<OpenAiCodexAccess, string>.AsError($"{SignInExpired} ({refreshed.Error})");
            if (string.IsNullOrWhiteSpace(refreshed.Value.AccountId) && string.IsNullOrWhiteSpace(cred.AccountId))
                return Result<OpenAiCodexAccess, string>.AsError(AccountMissing);

            var next = cred.WithTokens(refreshed.Value, _clock.GetUtcNow());
            // Persist before use: never hand out a token whose rotated refresh token is not on disk.
            var saved = await _store
                .SaveAsync(id, DysonSubjects.Local, next.ToJson(), CancellationToken.None)
                .ConfigureAwait(false);
            if (saved.IsError)
                return Result<OpenAiCodexAccess, string>.AsError($"Could not save the refreshed ChatGPT sign-in: {saved.Error}");

            return Result<OpenAiCodexAccess, string>.AsValue(new OpenAiCodexAccess(next.AccessToken, next.AccountId!));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Adopt a CLIProxy <c>codex-*.json</c>: one native refresh validates it, then the refreshed tokens are
    /// persisted. Refresh failure is an error (the caller still switches the provider row and says "use Connect").
    /// A successful refresh can rotate the token and break CLIProxy's copy.
    /// </summary>
    public async Task<Result<string, string>> ImportCredentialAsync(
        Guid credentialId,
        string subjectId,
        OpenAiCodexCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credentialId == Guid.Empty || string.IsNullOrWhiteSpace(subjectId))
            return Result<string, string>.AsError("Credential id and subject id are required.");
        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
            return Result<string, string>.AsError("The credential has no refresh token.");

        var gate = _gates.GetOrAdd(credentialId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await _oauth.RefreshAsync(credential.RefreshToken, CancellationToken.None).ConfigureAwait(false);
            if (refreshed.IsError)
                return Result<string, string>.AsError(refreshed.Error);

            var next = credential.WithTokens(refreshed.Value, _clock.GetUtcNow());
            if (string.IsNullOrWhiteSpace(next.AccountId))
                return Result<string, string>.AsError(AccountMissing);

            var saved = await _store
                .SaveAsync(credentialId, subjectId.Trim(), next.ToJson(), CancellationToken.None)
                .ConfigureAwait(false);
            return saved.IsError
                ? Result<string, string>.AsError($"Could not save the imported ChatGPT sign-in: {saved.Error}")
                : Result<string, string>.AsValue(next.Email ?? "");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<string, string>> GetEmailAsync(string handle, CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return Result<string, string>.AsError("Not a ChatGPT Codex credential handle.");

        var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return loaded.IsError
            ? Result<string, string>.AsError(loaded.Error)
            : Result<string, string>.AsValue(loaded.Value.Email ?? "");
    }

    public async Task<Result<bool, string>> IsConnectedAsync(string handle, CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return Result<bool, string>.AsValue(false);

        var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return Result<bool, string>.AsValue(loaded.IsSuccess && !string.IsNullOrWhiteSpace(loaded.Value.AccountId));
    }

    public async Task<VoidResult<string>> DisconnectAsync(string handle, CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return VoidResult<string>.AsError("Not a ChatGPT Codex credential handle.");

        var gate = _gates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Result<OpenAiCodexCredential, string>> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (row.IsError)
            return Result<OpenAiCodexCredential, string>.AsError(row.Error);
        if (row.Value is null)
            return Result<OpenAiCodexCredential, string>.AsError("No ChatGPT sign-in found. Use Connect on the ChatGPT Codex card.");

        var parsed = OpenAiCodexCredential.TryParse(row.Value);
        return parsed.IsError
            ? Result<OpenAiCodexCredential, string>.AsError($"{parsed.Error} Sign in again.")
            : parsed;
    }

    private async Task DropPendingAsync()
    {
        PendingLogin? pending;
        lock (_pendingLock)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is not null)
            await pending.Callback.DisposeAsync().ConfigureAwait(false);
    }

    private static string CallbackError(OpenAiCodexCallbackResult callback) =>
        callback.Error switch
        {
            "cancelled" => "ChatGPT sign-in cancelled.",
            "timed out" => "ChatGPT sign-in timed out. Start Connect again.",
            null or "" => "ChatGPT sign-in did not complete.",
            var other => $"ChatGPT sign-in failed ({other}).",
        };

    private sealed class PendingLogin(string state, string verifier, Guid credentialId, OpenAiCodexCallback callback)
    {
        public string State { get; } = state;
        public string Verifier { get; } = verifier;
        public Guid CredentialId { get; } = credentialId;
        public OpenAiCodexCallback Callback { get; } = callback;
    }
}

/// <summary>Access token plus the account id the request handler must send. Never log it.</summary>
public sealed record OpenAiCodexAccess(string AccessToken, string AccountId)
{
    public override string ToString() => "OpenAiCodexAccess(redacted)";
}

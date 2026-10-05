// Derived from router-for-me/CLIProxyAPI (MIT) internal/auth/xai/xai.go (refresh single-flight, RefreshLead)
//   @ 97f244b8ddb9cbf564b6e6faab0159102cca8617. See THIRD-PARTY-NOTICES.md.

using System.Collections.Concurrent;

namespace DysonHarness;

/// <summary>Device-flow details the UI shows to the user.</summary>
public sealed record XaiDeviceFlowStart(
    string State,
    string UserCode,
    string? VerificationUri,
    string? VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>Result of one <see cref="XaiGrokAuthService.PollAsync"/> call.</summary>
public sealed record XaiDeviceFlowPoll(
    XaiPollStatus Status,
    int RetryAfterSeconds,
    string? Email = null);

/// <summary>
/// Singleton owner of xAI OAuth state: pending device flows (in memory, lost on restart), access-token
/// resolution with per-credential single-flight refresh, and sign-out. State is keyed by credential handle
/// (<c>dyson-xai:&lt;guid&gt;</c>), not by subject, because <c>IHttpClientFactory</c> handlers have no circuit
/// scope. Persists rotated tokens BEFORE returning them. Never logs tokens.
/// </summary>
public sealed class XaiGrokAuthService(
    IDysonXaiCredentialStore store,
    XaiOAuthClient oauth,
    TimeProvider? clock = null)
{
    public const string HandlePrefix = "dyson-xai:";

    private readonly IDysonXaiCredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly XaiOAuthClient _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly ConcurrentDictionary<string, PendingFlow> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public static string HandleFor(Guid credentialId) => HandlePrefix + credentialId.ToString("D");

    public static bool TryParseHandle(string? value, out Guid credentialId)
    {
        credentialId = Guid.Empty;
        return value is not null
            && value.StartsWith(HandlePrefix, StringComparison.Ordinal)
            && Guid.TryParse(value.AsSpan(HandlePrefix.Length), out credentialId);
    }

    // ---- device flow ----------------------------------------------------------------------------------

    /// <summary>Request a device code. <paramref name="credentialId"/> is where the tokens land on success.</summary>
    public async Task<Result<XaiDeviceFlowStart, string>> BeginAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty)
            return Result<XaiDeviceFlowStart, string>.AsError("Credential id is required.");

        var started = await _oauth.StartDeviceFlowAsync(cancellationToken).ConfigureAwait(false);
        if (started.IsError)
            return Result<XaiDeviceFlowStart, string>.AsError(started.Error);

        var code = started.Value;
        var now = _clock.GetUtcNow();
        var interval = _oauth.InitialInterval(code);
        var lifetime = code.ExpiresIn > 0
            ? TimeSpan.FromSeconds(code.ExpiresIn)
            : XaiGrokClientProfile.MaxPollDuration;
        if (lifetime > XaiGrokClientProfile.MaxPollDuration)
            lifetime = XaiGrokClientProfile.MaxPollDuration;

        var state = Guid.NewGuid().ToString("N");
        DropExpired(now);
        _pending[state] = new PendingFlow(code, credentialId, interval, now + interval, now + lifetime);

        return Result<XaiDeviceFlowStart, string>.AsValue(new XaiDeviceFlowStart(
            state,
            code.UserCode,
            code.VerificationUri,
            code.VerificationUriComplete,
            (int)lifetime.TotalSeconds,
            (int)Math.Ceiling(interval.TotalSeconds)));
    }

    /// <summary>
    /// Poll a pending flow. Does not touch the network before the flow's next allowed poll time. On success the
    /// credential row is saved (awaited) and the flow is removed. Expired/denied/failed flows are error Results.
    /// </summary>
    public async Task<Result<XaiDeviceFlowPoll, string>> PollAsync(
        string state,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state) || !_pending.TryGetValue(state.Trim(), out var flow))
            return Result<XaiDeviceFlowPoll, string>.AsError("No pending xAI sign-in; start Connect again.");
        if (string.IsNullOrWhiteSpace(subjectId))
            return Result<XaiDeviceFlowPoll, string>.AsError("Subject id is required.");

        state = state.Trim();
        await flow.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (now >= flow.ExpiresAt)
            {
                _pending.TryRemove(state, out _);
                return Result<XaiDeviceFlowPoll, string>.AsError("xAI sign-in code expired; start Connect again.");
            }

            if (now < flow.NextPollAt)
                return Pending(flow, now);

            var polled = await _oauth.PollOnceAsync(flow.Code, cancellationToken).ConfigureAwait(false);
            if (polled.IsError)
            {
                // Cancellation and transient transport errors keep the flow; protocol errors end it.
                if (cancellationToken.IsCancellationRequested)
                    return Result<XaiDeviceFlowPoll, string>.AsError(polled.Error);
                if (polled.Exception is null)
                    _pending.TryRemove(state, out _);
                return Result<XaiDeviceFlowPoll, string>.AsError(polled.Error);
            }

            switch (polled.Value.Status)
            {
                case XaiPollStatus.SlowDown:
                    flow.Interval = _oauth.SlowDown(flow.Interval);
                    flow.NextPollAt = now + flow.Interval;
                    return Pending(flow, now, XaiPollStatus.SlowDown);
                case XaiPollStatus.Pending:
                    flow.NextPollAt = now + flow.Interval;
                    return Pending(flow, now);
            }

            var credential = XaiCredential.FromTokens(polled.Value.Tokens!, now, flow.Code.TokenEndpoint);
            var saved = await _store
                .SaveAsync(flow.CredentialId, subjectId.Trim(), credential.ToJson(), CancellationToken.None)
                .ConfigureAwait(false);
            _pending.TryRemove(state, out _);
            if (saved.IsError)
                return Result<XaiDeviceFlowPoll, string>.AsError($"Could not save the xAI sign-in: {saved.Error}");

            return Result<XaiDeviceFlowPoll, string>.AsValue(
                new XaiDeviceFlowPoll(XaiPollStatus.Complete, 0, credential.Email));
        }
        finally
        {
            flow.Gate.Release();
        }
    }

    /// <summary>Forget a pending flow (user pressed Cancel).</summary>
    public void CancelFlow(string? state)
    {
        if (!string.IsNullOrWhiteSpace(state))
            _pending.TryRemove(state.Trim(), out _);
    }

    // ---- tokens ---------------------------------------------------------------------------------------

    /// <summary>
    /// Current access token for <paramref name="handle"/>, refreshing within <see cref="XaiGrokClientProfile.RefreshLead"/>
    /// of expiry. When <paramref name="rejectedAccessToken"/> is the token xAI just refused, a refresh is forced
    /// unless another caller already rotated it (so N concurrent 401s cause one refresh).
    /// </summary>
    public async Task<Result<string, string>> GetAccessTokenAsync(
        string handle,
        string? rejectedAccessToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return Result<string, string>.AsError("Not an xAI credential handle.");

        var gate = _gates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (loaded.IsError)
                return Result<string, string>.AsError(loaded.Error);

            var cred = loaded.Value;
            var now = _clock.GetUtcNow();
            var rejected = !string.IsNullOrEmpty(rejectedAccessToken)
                && string.Equals(cred.AccessToken, rejectedAccessToken, StringComparison.Ordinal);
            if (!rejected && !cred.NeedsRefresh(now, XaiGrokClientProfile.RefreshLead))
                return Result<string, string>.AsValue(cred.AccessToken);

            if (string.IsNullOrWhiteSpace(cred.RefreshToken))
                return Result<string, string>.AsError(SignInExpired);

            // CancellationToken.None: a dropped caller must not abandon a refresh that may rotate the token.
            var refreshed = await _oauth
                .RefreshAsync(cred.RefreshToken, cred.TokenEndpoint, CancellationToken.None)
                .ConfigureAwait(false);
            if (refreshed.IsError)
                return Result<string, string>.AsError($"{SignInExpired} ({refreshed.Error})");

            var next = cred.WithTokens(refreshed.Value, _clock.GetUtcNow(), cred.TokenEndpoint);
            // Persist before use: never hand out a token whose rotated refresh token is not on disk.
            // SubjectId is ignored on update (the row exists); Local satisfies the non-blank check.
            var saved = await _store
                .SaveAsync(id, DysonSubjects.Local, next.ToJson(), CancellationToken.None)
                .ConfigureAwait(false);
            if (saved.IsError)
                return Result<string, string>.AsError($"Could not save the refreshed xAI sign-in: {saved.Error}");

            return Result<string, string>.AsValue(next.AccessToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Adopt an existing credential (e.g. a CLIProxy <c>xai-*.json</c>): one native refresh validates it, then the
    /// refreshed tokens are persisted under <paramref name="credentialId"/>. Returns the account email (may be empty).
    /// Note: if xAI rotates refresh tokens, this invalidates the donor copy.
    /// </summary>
    public async Task<Result<string, string>> ImportCredentialAsync(
        Guid credentialId,
        string subjectId,
        XaiCredential credential,
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
            var refreshed = await _oauth
                .RefreshAsync(credential.RefreshToken, credential.TokenEndpoint, CancellationToken.None)
                .ConfigureAwait(false);
            if (refreshed.IsError)
                return Result<string, string>.AsError(refreshed.Error);

            var next = credential.WithTokens(refreshed.Value, _clock.GetUtcNow(), credential.TokenEndpoint)
                with { BaseUrl = XaiGrokClientProfile.ChatProxyBaseUrl };
            var saved = await _store
                .SaveAsync(credentialId, subjectId.Trim(), next.ToJson(), CancellationToken.None)
                .ConfigureAwait(false);
            return saved.IsError
                ? Result<string, string>.AsError($"Could not save the imported xAI sign-in: {saved.Error}")
                : Result<string, string>.AsValue(next.Email ?? "");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Signed-in account email (may be empty) for UI display.</summary>
    public async Task<Result<string, string>> GetEmailAsync(string handle, CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return Result<string, string>.AsError("Not an xAI credential handle.");

        var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return loaded.IsError
            ? Result<string, string>.AsError(loaded.Error)
            : Result<string, string>.AsValue(loaded.Value.Email ?? "");
    }

    /// <summary>True when a parseable credential row exists.</summary>
    public async Task<Result<bool, string>> IsConnectedAsync(string handle, CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return Result<bool, string>.AsValue(false);

        var loaded = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return Result<bool, string>.AsValue(loaded.IsSuccess);
    }

    /// <summary>Sign out: delete the credential row.</summary>
    public async Task<VoidResult<string>> DisconnectAsync(string handle, CancellationToken cancellationToken = default)
    {
        if (!TryParseHandle(handle, out var id))
            return VoidResult<string>.AsError("Not an xAI credential handle.");

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

    // ---- internals ------------------------------------------------------------------------------------

    internal const string SignInExpired = "xAI sign-in expired, reconnect in Settings > Models";

    private async Task<Result<XaiCredential, string>> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (row.IsError)
            return Result<XaiCredential, string>.AsError(row.Error);
        if (row.Value is null)
            return Result<XaiCredential, string>.AsError("No xAI sign-in found, connect in Settings > Models");

        var parsed = XaiCredential.TryParse(row.Value);
        return parsed.IsError
            ? Result<XaiCredential, string>.AsError($"{parsed.Error} Sign in again in Settings > Models")
            : parsed;
    }

    private static Result<XaiDeviceFlowPoll, string> Pending(
        PendingFlow flow,
        DateTimeOffset now,
        XaiPollStatus status = XaiPollStatus.Pending) =>
        Result<XaiDeviceFlowPoll, string>.AsValue(new XaiDeviceFlowPoll(
            status,
            (int)Math.Ceiling(Math.Max(0, (flow.NextPollAt - now).TotalSeconds))));

    private void DropExpired(DateTimeOffset now)
    {
        foreach (var (key, flow) in _pending)
        {
            if (now >= flow.ExpiresAt)
                _pending.TryRemove(key, out _);
        }
    }

    private sealed class PendingFlow(
        XaiDeviceCode code,
        Guid credentialId,
        TimeSpan interval,
        DateTimeOffset nextPollAt,
        DateTimeOffset expiresAt)
    {
        public XaiDeviceCode Code { get; } = code;
        public Guid CredentialId { get; } = credentialId;
        public TimeSpan Interval { get; set; } = interval;
        public DateTimeOffset NextPollAt { get; set; } = nextPollAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

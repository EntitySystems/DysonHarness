using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

public class OpenAiCodexAuthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Handle_round_trips()
    {
        var id = Guid.NewGuid();
        Assert.True(OpenAiCodexAuthService.TryParseHandle(OpenAiCodexAuthService.HandleFor(id), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(OpenAiCodexAuthService.TryParseHandle("dyson-xai:" + id, out _));
        Assert.False(OpenAiCodexAuthService.TryParseHandle("openai-codex", out _));
    }

    [Fact]
    public async Task Fresh_token_inside_the_lead_is_returned_without_a_refresh()
    {
        var (auth, store, http, _) = Rig();
        var id = Guid.NewGuid();
        store.Put(id, Cred(Now.AddHours(1), "access-1", "refresh-1"));

        var token = await auth.GetAccessTokenAsync(OpenAiCodexAuthService.HandleFor(id));

        Assert.True(token.IsSuccess);
        Assert.Equal("access-1", token.Value.AccessToken);
        Assert.Equal("acct_1", token.Value.AccountId);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Inside_the_five_minute_lead_refreshes_and_persists_before_returning()
    {
        var (auth, store, http, clock) = Rig(RefreshJson("access-new", "refresh-new"));
        var id = Guid.NewGuid();
        store.Put(id, Cred(Now.AddMinutes(4), "access-old", "refresh-old"));
        string? savedBeforeReturn = null;
        store.OnSave = (_, json) => savedBeforeReturn = json;

        var token = await auth.GetAccessTokenAsync(OpenAiCodexAuthService.HandleFor(id));

        Assert.True(token.IsSuccess);
        Assert.Equal("access-new", token.Value.AccessToken);
        Assert.NotNull(savedBeforeReturn);
        Assert.Contains("refresh-new", savedBeforeReturn);
        Assert.Contains("refresh_token=refresh-old", http.Requests[0].Body);
        Assert.Equal(Now, clock.Now);
    }

    [Fact]
    public async Task Failed_save_returns_an_error_and_no_token()
    {
        var (auth, store, _, _) = Rig(RefreshJson("access-new", "refresh-new"));
        var id = Guid.NewGuid();
        store.Put(id, Cred(Now.AddMinutes(1), "access-old", "refresh-old"));
        store.FailSaves = true;

        var token = await auth.GetAccessTokenAsync(OpenAiCodexAuthService.HandleFor(id));

        Assert.True(token.IsError);
        Assert.Contains("disk full", token.Error);
        Assert.DoesNotContain("access-new", token.Error);
    }

    [Fact]
    public async Task Two_concurrent_calls_cause_one_refresh()
    {
        var (auth, store, http, _) = Rig(RefreshJson("access-new", "refresh-new"));
        var id = Guid.NewGuid();
        store.Put(id, Cred(Now.AddMinutes(1), "access-old", "refresh-old"));
        var handle = OpenAiCodexAuthService.HandleFor(id);

        var first = auth.GetAccessTokenAsync(handle);
        var second = auth.GetAccessTokenAsync(handle);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, http.Requests.Count);
    }

    [Fact]
    public async Task Missing_account_id_is_not_a_sendable_token()
    {
        var (auth, store, http, _) = Rig();
        var id = Guid.NewGuid();
        store.Put(id, Cred(Now.AddHours(1), "access-1", "refresh-1", accountId: ""));

        var token = await auth.GetAccessTokenAsync(OpenAiCodexAuthService.HandleFor(id));

        Assert.True(token.IsError);
        Assert.Contains("account id", token.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Complete_exchanges_the_callback_and_saves_before_returning_the_email()
    {
        OpenAiCodexCallbackResult? posted = null;
        var callback = new ScriptedCallback();
        var (auth, store, _, _) = Rig(
            TokenJson("access-1", "refresh-1"),
            () => callback);
        var id = Guid.NewGuid();

        var started = await auth.BeginAsync(id);
        Assert.True(started.IsSuccess);
        Assert.Contains("client_id=", started.Value.AuthorizeUrl);

        callback.Complete(new OpenAiCodexCallbackResult(true, "the-code", started.Value.State, null));
        var done = await auth.CompleteAsync(started.Value.State, DysonSubjects.Local);

        Assert.True(done.IsSuccess);
        Assert.Equal("ada@example.com", done.Value);
        var saved = OpenAiCodexCredential.TryParse(store.Peek(id));
        Assert.True(saved.IsSuccess);
        Assert.Equal("access-1", saved.Value.AccessToken);
        Assert.Equal("acct_1", saved.Value.AccountId);
        posted = callback.Last;
        Assert.NotNull(posted);
    }

    [Fact]
    public void Callback_request_line_requires_code_and_state_and_fails_on_error()
    {
        var ok = OpenAiCodexCallback.ParseRequestLine("GET /auth/callback?code=abc&state=xyz HTTP/1.1");
        Assert.True(ok.Ok);
        Assert.Equal("abc", ok.Code);
        Assert.Equal("xyz", ok.State);

        var failed = OpenAiCodexCallback.ParseRequestLine("GET /auth/callback?error=access_denied&state=xyz HTTP/1.1");
        Assert.False(failed.Ok);
        Assert.Equal("access_denied", failed.Error);

        var other = OpenAiCodexCallback.ParseRequestLine("GET /success HTTP/1.1");
        Assert.False(other.Ok);
        Assert.Null(other.Error);
    }

    [Fact]
    public void Bind_failure_distinguishes_a_reserved_port_from_a_busy_one()
    {
        Assert.Contains("reserved", OpenAiCodexCallback.DescribeBindFailure(new SocketException((int)SocketError.AccessDenied)));
        Assert.Contains("something else", OpenAiCodexCallback.DescribeBindFailure(new SocketException((int)SocketError.AddressAlreadyInUse)));
    }

    [Fact]
    public async Task Begin_returns_the_injected_bind_error_and_does_not_listen()
    {
        var (auth, _, _, _) = Rig(callbackFactory: () => new ScriptedCallback
        {
            StartResult = Result<bool, string>.AsError(OpenAiCodexCallback.PortInUseMessage),
        });

        var started = await auth.BeginAsync(Guid.NewGuid());

        Assert.True(started.IsError);
        Assert.Equal(OpenAiCodexCallback.PortInUseMessage, started.Error);
    }

    private static (OpenAiCodexAuthService Auth, InMemoryOpenAiCodexCredentialStore Store, FakeXaiHttpHandler Http, FixedTimeProvider Clock) Rig(
        string? tokenJson = null,
        Func<OpenAiCodexCallback>? callbackFactory = null)
    {
        var http = new FakeXaiHttpHandler(_ => FakeXaiHttpHandler.Json(tokenJson ?? RefreshJson("access-new", "refresh-new")));
        var clock = new FixedTimeProvider(Now);
        var oauth = new OpenAiCodexOAuthClient(() => new HttpClient(http, disposeHandler: false), clock);
        var store = new InMemoryOpenAiCodexCredentialStore();
        var auth = new OpenAiCodexAuthService(store, oauth, clock, callbackFactory);
        return (auth, store, http, clock);
    }

    private static string Cred(DateTimeOffset expires, string access, string refresh, string accountId = "acct_1") =>
        new OpenAiCodexCredential
        {
            AccessToken = access,
            RefreshToken = refresh,
            AccountId = accountId,
            Email = "ada@example.com",
            Expired = OpenAiCodexCredential.FormatUtc(expires),
        }.ToJson();

    private static string RefreshJson(string access, string refresh) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["id_token"] = IdToken(),
            ["expires_in"] = 3600,
        });

    private static string TokenJson(string access, string refresh) => RefreshJson(access, refresh);

    private static string IdToken()
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            """{"email":"ada@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"acct_1","chatgpt_plan_type":"plus"}}"""));
        return "h." + payload.TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";
    }

    private sealed class ScriptedCallback : OpenAiCodexCallback
    {
        private readonly TaskCompletionSource<OpenAiCodexCallbackResult> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptedCallback()
            : base(() => Result<TcpListener, string>.AsError("scripted"))
        {
            StartOverride = () => StartResult;
            WaitOverride = (_, cancellationToken) => _done.Task.WaitAsync(cancellationToken);
        }

        public Result<bool, string> StartResult { get; init; } = Result<bool, string>.AsValue(true);
        public OpenAiCodexCallbackResult? Last { get; private set; }

        public void Complete(OpenAiCodexCallbackResult result)
        {
            Last = result;
            _done.TrySetResult(result);
        }

        public new ValueTask DisposeAsync()
        {
            _done.TrySetResult(new OpenAiCodexCallbackResult(false, null, null, "cancelled"));
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class InMemoryOpenAiCodexCredentialStore : IDysonOpenAiCodexCredentialStore
{
    private readonly ConcurrentDictionary<Guid, string> _rows = new();

    public bool FailSaves { get; set; }
    public Action<Guid, string>? OnSave { get; set; }

    public string? Peek(Guid id) => _rows.TryGetValue(id, out var v) ? v : null;
    public void Put(Guid id, string json) => _rows[id] = json;

    public Task<Result<string?, string>> GetAsync(Guid credentialId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<string?, string>.AsValue(Peek(credentialId)));

    public Task<VoidResult<string>> SaveAsync(Guid credentialId, string subjectId, string json, CancellationToken cancellationToken = default)
    {
        if (FailSaves)
            return Task.FromResult(VoidResult<string>.AsError("disk full"));
        _rows[credentialId] = json;
        OnSave?.Invoke(credentialId, json);
        return Task.FromResult(VoidResult<string>.Success);
    }

    public Task<VoidResult<string>> DeleteAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        _rows.TryRemove(credentialId, out _);
        return Task.FromResult(VoidResult<string>.Success);
    }
}

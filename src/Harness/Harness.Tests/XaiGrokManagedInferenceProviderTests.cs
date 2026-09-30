using System.Net;
using DysonHarness;

namespace Harness.Tests;

public class XaiGrokManagedInferenceProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        public required XaiGrokManagedInferenceProvider Provider { get; init; }
        public required InMemoryXaiCredentialStore Store { get; init; }
        public required DysonModelRepository Models { get; init; }
        public required FakeXaiHttpHandler Chat { get; init; }
        public required FixedTimeProvider Clock { get; init; }
        public required Microsoft.Data.Sqlite.SqliteConnection Conn { get; init; }
        public void Dispose() => Conn.Dispose();
    }

    private static Rig CreateRig(Func<CapturedRequest, HttpResponseMessage>? chat = null)
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        var clock = new FixedTimeProvider(T0);
        var store = new InMemoryXaiCredentialStore();
        var oauthHttp = new FakeXaiHttpHandler(req =>
        {
            var url = req.Uri.AbsoluteUri;
            if (url.Contains("openid-configuration"))
                return FakeXaiHttpHandler.Json(
                    """{"device_authorization_endpoint":"https://auth.x.ai/d","token_endpoint":"https://auth.x.ai/t"}""");
            if (url.EndsWith("/d", StringComparison.Ordinal))
                return FakeXaiHttpHandler.Json(
                    """{"device_code":"dc","user_code":"ABCD","verification_uri":"https://x.ai/d","verification_uri_complete":"https://x.ai/d?c=ABCD","expires_in":600,"interval":5}""");
            return FakeXaiHttpHandler.Json("""{"access_token":"at","refresh_token":"rt","expires_in":3600}""");
        });
        var auth = new XaiGrokAuthService(
            store,
            new XaiOAuthClient(() => new HttpClient(oauthHttp, disposeHandler: false), clock),
            clock);
        var chatInner = new FakeXaiHttpHandler(chat ?? (_ => FakeXaiHttpHandler.Json(
            """{"data":[{"id":"grok-4.7"},{"id":"grok-imagine-image-2.0"},{"id":"grok-9-new"}]}""")));
        var http = new HttpClient(new XaiGrokRequestHandler(auth) { InnerHandler = chatInner });
        var models = DysonTempDb.Models(accessor);
        var provider = new XaiGrokManagedInferenceProvider(
            auth, http, models, DysonFixedLocalSubjectContext.Instance, DysonTempDb.Settings(accessor));
        return new Rig { Provider = provider, Store = store, Models = models, Chat = chatInner, Clock = clock, Conn = conn };
    }

    private static async Task SignInAsync(Rig rig)
    {
        Assert.True((await rig.Provider.ImportAsync()).IsSuccess);
        Assert.True((await rig.Provider.AcceptConsentAsync()).IsSuccess);
        var begin = await rig.Provider.BeginConnectionAsync();
        Assert.True(begin.IsSuccess, begin.IsError ? begin.Error : null);
        rig.Clock.Now = T0.AddSeconds(6);
        var done = await rig.Provider.CompleteConnectionAsync(begin.Value.State);
        Assert.True(done.IsSuccess, done.IsError ? done.Error : null);
        Assert.True(done.Value.IsComplete);
    }

    [Fact]
    public async Task Import_creates_one_row_with_handle_key_and_code_owned_base_url_and_is_idempotent()
    {
        using var rig = CreateRig();

        var first = await rig.Provider.ImportAsync();
        var second = await rig.Provider.ImportAsync();

        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        var rows = (await rig.Models.ListProvidersAsync()).Value
            .Where(p => p.ManagedSource == DysonManagedSources.XaiGrok).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(XaiGrokClientProfile.ChatProxyBaseUrl, row.BaseUrl);
        Assert.Equal(DysonOpenAiApiModes.Responses, row.OpenAiApiMode);
        Assert.True(XaiGrokAuthService.TryParseHandle(row.ApiKey, out _));
        Assert.Empty(row.Slugs);
    }

    [Fact]
    public async Task Begin_requires_import_and_consent()
    {
        using var rig = CreateRig();

        var noConsent = await rig.Provider.BeginConnectionAsync();
        Assert.True(noConsent.IsError);
        Assert.Contains("notice", noConsent.Error);

        Assert.True((await rig.Provider.AcceptConsentAsync()).IsSuccess);
        Assert.True(await rig.Provider.HasConsentAsync());
        var notImported = await rig.Provider.BeginConnectionAsync();
        Assert.True(notImported.IsError);
        Assert.Contains("Import", notImported.Error);
    }

    [Fact]
    public async Task Device_flow_returns_code_link_and_interval_then_stores_the_credential_under_the_row_handle()
    {
        using var rig = CreateRig();
        var id = (await rig.Provider.ImportAsync()).Value;
        await rig.Provider.AcceptConsentAsync();

        var begin = await rig.Provider.BeginConnectionAsync(openBrowser: false);

        Assert.True(begin.IsSuccess);
        Assert.Equal("ABCD", begin.Value.UserCode);
        Assert.Equal("https://x.ai/d?c=ABCD", begin.Value.AuthUrl);
        Assert.Equal(5, begin.Value.PollIntervalSeconds);
        Assert.Equal("device", begin.Value.Flow);

        var early = await rig.Provider.CompleteConnectionAsync(begin.Value.State);
        Assert.Equal("pending", early.Value.Status);
        Assert.False(early.Value.IsComplete);

        rig.Clock.Now = T0.AddSeconds(6);
        var done = await rig.Provider.CompleteConnectionAsync(begin.Value.State);
        Assert.True(done.Value.IsComplete);

        var row = (await rig.Models.ListProvidersAsync()).Value.Single(p => p.Id == id);
        XaiGrokAuthService.TryParseHandle(row.ApiKey, out var credentialId);
        Assert.NotNull(rig.Store.Peek(credentialId));
        var summary = await rig.Provider.GetAccountSummaryAsync();
        Assert.True(summary.Value.Connected);
        Assert.Equal("1.0.13", summary.Value.ClientVersion);
    }

    [Fact]
    public async Task Verify_uses_the_live_list_excluding_imagine_and_gives_unknown_models_no_effort_levels()
    {
        using var rig = CreateRig();
        await SignInAsync(rig);

        var verify = await rig.Provider.VerifyConnectionAsync();

        Assert.True(verify.IsSuccess, verify.IsError ? verify.Error : null);
        Assert.Equal(["grok-4.7", "grok-9-new"], verify.Value.Slugs.ToArray());
        Assert.Contains("live", verify.Value.Note);
        var request = rig.Chat.Requests.Single();
        Assert.Equal("https://cli-chat-proxy.grok.com/v1/models", request.Uri.ToString());
        Assert.Equal("Bearer at", request.Headers["Authorization"]);

        var row = (await rig.Models.ListProvidersAsync()).Value.Single(p => p.ManagedSource == DysonManagedSources.XaiGrok);
        var g47 = row.Slugs.Single(s => s.Slug == "grok-4.7");
        Assert.Equal("high", g47.DefaultReasoningEffort);
        Assert.Equal(["low", "medium", "high", "xhigh"], g47.ReasoningModes);
        Assert.Empty(row.Slugs.Single(s => s.Slug == "grok-9-new").ReasoningModes);
    }

    [Fact]
    public async Task Verify_falls_back_to_the_bundled_table_when_models_is_unavailable()
    {
        using var rig = CreateRig(_ => FakeXaiHttpHandler.Json("nope", HttpStatusCode.NotFound));
        await SignInAsync(rig);

        var verify = await rig.Provider.VerifyConnectionAsync();

        Assert.True(verify.IsSuccess);
        Assert.Equal(XaiGrokModelCatalog.Bundled.Count, verify.Value.SlugCount);
        Assert.Contains("bundled", verify.Value.Note);
        Assert.DoesNotContain(verify.Value.Slugs, s => s.StartsWith("grok-imagine-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_when_signed_out_is_an_error_and_makes_no_models_request()
    {
        using var rig = CreateRig();
        await rig.Provider.ImportAsync();

        var verify = await rig.Provider.VerifyConnectionAsync();

        Assert.True(verify.IsError);
        Assert.Empty(rig.Chat.Requests);
    }

    [Fact]
    public async Task Disconnect_deletes_the_credential_but_keeps_the_provider_row_and_slugs()
    {
        using var rig = CreateRig();
        await SignInAsync(rig);
        await rig.Provider.VerifyConnectionAsync();

        Assert.True((await rig.Provider.DisconnectAsync()).IsSuccess);

        var row = (await rig.Models.ListProvidersAsync()).Value.Single(p => p.ManagedSource == DysonManagedSources.XaiGrok);
        Assert.NotEmpty(row.Slugs);
        XaiGrokAuthService.TryParseHandle(row.ApiKey, out var id);
        Assert.Null(rig.Store.Peek(id));
        Assert.False((await rig.Provider.GetAccountSummaryAsync()).Value.Connected);
    }

    private static async Task<Guid> SeedLegacyAsync(Rig rig)
    {
        var id = (await rig.Models.UpsertManagedProviderAsync(
            DysonManagedSources.CliProxyGrok,
            "Grok Build (CLIProxy)",
            "http://127.0.0.1:8317/v1",
            "cliproxy-key",
            DysonOpenAiApiModes.Responses,
            [new ManagedSlugSpec("grok-4.7", "Grok 4.7", "high", ["low", "medium", "high", "xhigh"])])).Value;
        var row = (await rig.Models.ListProvidersAsync()).Value.Single(p => p.Id == id);
        Assert.True((await rig.Models.SetDefaultSlugAsync(row.Slugs.Single().Id)).IsSuccess);
        return id;
    }

    [Fact]
    public async Task Switch_converts_in_place_keeping_ids_and_adopts_the_cliproxy_login()
    {
        using var rig = CreateRig();
        var providerId = await SeedLegacyAsync(rig);
        var slugId = (await rig.Models.ListProvidersAsync()).Value.Single().Slugs.Single().Id;
        var dir = Directory.CreateTempSubdirectory("xai-auths").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "xai-me.json"), """
                {"type":"xai","access_token":"old-at","refresh_token":"old-rt","expired":"2026-09-30T11:00:00Z",
                 "email":"me@x.ai","base_url":"https://api.x.ai/v1","token_endpoint":"https://auth.x.ai/t",
                 "headers":{"x-grok-client-version":"0.2.120"}}
                """);
            await rig.Provider.AcceptConsentAsync();

            var outcome = await rig.Provider.SwitchFromCliProxyAsync(dir);

            Assert.True(outcome.IsSuccess, outcome.IsError ? outcome.Error : null);
            Assert.True(outcome.Value.LoginImported);
            Assert.Equal(providerId, outcome.Value.ProviderId);

            var rows = (await rig.Models.ListProvidersAsync()).Value;
            var row = Assert.Single(rows);
            Assert.Equal(providerId, row.Id);
            Assert.Equal(DysonManagedSources.XaiGrok, row.ManagedSource);
            Assert.Equal(XaiGrokClientProfile.ChatProxyBaseUrl, row.BaseUrl);
            var slug = Assert.Single(row.Slugs);
            Assert.Equal(slugId, slug.Id);
            Assert.True(slug.IsDefault);
            Assert.True(slug.IsEnabled);

            XaiGrokAuthService.TryParseHandle(row.ApiKey, out var credentialId);
            var stored = XaiCredential.TryParse(rig.Store.Peek(credentialId)).Value;
            Assert.Equal("at", stored.AccessToken);
            Assert.Equal("rt", stored.RefreshToken);
            Assert.Equal("me@x.ai", stored.Email);
            Assert.Equal(XaiGrokClientProfile.ChatProxyBaseUrl, stored.BaseUrl);
            Assert.DoesNotContain("0.2.120", rig.Store.Peek(credentialId));
            Assert.True(File.Exists(Path.Combine(dir, "xai-me.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Switch_without_a_cliproxy_login_still_converts_and_asks_for_connect()
    {
        using var rig = CreateRig();
        await SeedLegacyAsync(rig);
        var emptyDir = Directory.CreateTempSubdirectory("xai-auths-empty").FullName;
        try
        {
            var outcome = await rig.Provider.SwitchFromCliProxyAsync(emptyDir);

            Assert.True(outcome.IsSuccess);
            Assert.False(outcome.Value.LoginImported);
            Assert.Contains("Connect", outcome.Value.Note);
            Assert.Equal(
                DysonManagedSources.XaiGrok,
                (await rig.Models.ListProvidersAsync()).Value.Single().ManagedSource);
            Assert.False((await rig.Provider.GetAccountSummaryAsync()).Value.Connected);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    public async Task Switch_errors_without_a_legacy_row_or_when_native_already_exists()
    {
        using var rig = CreateRig();
        Assert.True((await rig.Provider.SwitchFromCliProxyAsync("missing-dir")).IsError);

        await SeedLegacyAsync(rig);
        await rig.Provider.ImportAsync();
        var both = await rig.Provider.SwitchFromCliProxyAsync("missing-dir");
        Assert.True(both.IsError);
        Assert.Contains("already exists", both.Error);
    }

    [Fact]
    public async Task ConvertManagedSource_errors_when_target_exists_or_source_missing()
    {
        using var rig = CreateRig();
        Assert.True((await rig.Models.ConvertManagedSourceAsync(
            "cliproxy-grok", "xai-grok", "n", "https://x", "k", DysonOpenAiApiModes.Responses)).IsError);

        await rig.Models.UpsertManagedProviderAsync(
            "cliproxy-grok", "a", "http://127.0.0.1:8317/v1", "k", DysonOpenAiApiModes.Responses, []);
        await rig.Models.UpsertManagedProviderAsync(
            "xai-grok", "b", "https://x", "k", DysonOpenAiApiModes.Responses, []);

        var clash = await rig.Models.ConvertManagedSourceAsync(
            "cliproxy-grok", "xai-grok", "n", "https://x", "k", DysonOpenAiApiModes.Responses);
        Assert.True(clash.IsError);
        Assert.Contains("already exists", clash.Error);
    }

    [Fact]
    public async Task Files_upload_is_skipped_for_xai_grok_but_attempted_for_other_managed_providers()
    {
        var calls = 0;
        var http = new HttpClient(new FakeXaiHttpHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return FakeXaiHttpHandler.Json("""{"id":"file_1"}""");
        }));
        static OpenAiCompatibleAgentProvider Make(string source) => new(
            new DysonModelProviderEntity
            {
                Id = Guid.NewGuid(), DisplayName = "P", ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = "https://example.invalid/v1", ApiKey = "k", ManagedSource = source,
                OpenAiApiMode = DysonOpenAiApiModes.Responses,
            },
            null);
        var attachment = new DysonBinaryAttachment
        {
            FileName = "a.png", Extension = ".png", MimeType = "image/png", Base64Data = Convert.ToBase64String([1, 2, 3]),
        };

        await OpenAiFilesClient.EnsureBinaryFileIdsAsync(http, Make(DysonManagedSources.XaiGrok), [attachment]);
        Assert.Equal(0, calls);

        await OpenAiFilesClient.EnsureBinaryFileIdsAsync(http, Make(DysonManagedSources.CliProxyCodex), [attachment]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Catalog_lists_the_native_provider_next_to_the_legacy_cliproxy_one()
    {
        using var rig = CreateRig();
        var catalog = new ManagedInferenceProviderCatalog(
            new DysonCliProxyHost(new HttpClient()),
            new HttpClient(),
            rig.Models,
            DysonTempDb.Settings(DysonTempDb.OpenMemoryAccessor(out var c2)),
            new XaiGrokAuthService(
                rig.Store,
                new XaiOAuthClient(() => new HttpClient())),
            DysonFixedLocalSubjectContext.Instance);
        using var _ = c2;

        Assert.Same(catalog.XaiGrok, catalog.FindBySource("xai-grok"));
        Assert.NotNull(catalog.FindBySource("cliproxy-grok"));
        Assert.NotSame(catalog.FindBySource("xai-grok"), catalog.FindBySource("cliproxy-grok"));
    }
}

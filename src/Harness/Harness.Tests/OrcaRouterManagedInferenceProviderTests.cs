using System.Net;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// OrcaRouter catalog parse/map/filter + Import. No live HTTP.
/// </summary>
public class OrcaRouterManagedInferenceProviderTests
{
    [Fact]
    public void Identity_is_orcarouter()
    {
        var provider = CreateProvider(new FailOnRequestHandler());
        Assert.Equal("orcarouter", provider.ManagedSource);
        Assert.Equal(DysonManagedSources.OrcaRouter, provider.ManagedSource);
        Assert.Equal("OrcaRouter", provider.DisplayName);
    }

    [Fact]
    public void Map_keeps_text_output_modality_and_uses_id_as_display_name()
    {
        var model = MapOne("""
            {
              "id": "anthropic/claude-sonnet-4",
              "name": "Should Not Be Used",
              "owned_by": "anthropic",
              "architecture": { "output_modalities": ["text"] }
            }
            """);

        Assert.Equal("anthropic/claude-sonnet-4", model.Slug);
        Assert.Equal("anthropic/claude-sonnet-4", model.DisplayName);
        Assert.Empty(model.EffortLevels);
        Assert.Null(model.DefaultReasoningEffort);
    }

    [Fact]
    public void Map_keeps_text_plus_other_output_modalities()
    {
        var model = MapOne("""
            {
              "id": "google/gemini-2.5-flash",
              "architecture": { "output_modalities": ["text", "image"] }
            }
            """);
        Assert.Equal("google/gemini-2.5-flash", model.Slug);
    }

    [Fact]
    public void Map_drops_image_only_and_video_prefixes()
    {
        var page = OrcaRouterManagedInferenceProvider.ParseModels("""
            {
              "data": [
                {
                  "id": "google/imagen-3",
                  "architecture": { "output_modalities": ["image"] }
                },
                {
                  "id": "kling/video-1",
                  "architecture": { "output_modalities": ["text"] }
                },
                {
                  "id": "byteplus/seedance",
                  "supported_endpoint_types": ["openai"]
                },
                {
                  "id": "openai/gpt-4o-mini",
                  "architecture": { "output_modalities": ["text"] }
                }
              ]
            }
            """);

        var model = Assert.Single(page);
        Assert.Equal("openai/gpt-4o-mini", model.Slug);
    }

    [Fact]
    public void Map_missing_architecture_keeps_openai_endpoint_only()
    {
        var openai = MapOne("""
            {
              "id": "vendor/chat",
              "owned_by": "vendor",
              "supported_endpoint_types": ["openai"]
            }
            """);
        Assert.Equal("vendor/chat", openai.Slug);

        var page = OrcaRouterManagedInferenceProvider.ParseModels("""
            {
              "data": [
                { "id": "vendor/anthropic-only", "supported_endpoint_types": ["anthropic"] },
                { "id": "vendor/no-endpoints" },
                { "id": "vendor/openai", "supported_endpoint_types": ["openai"] }
              ]
            }
            """);
        var kept = Assert.Single(page);
        Assert.Equal("vendor/openai", kept.Slug);
    }

    [Fact]
    public void Parse_skips_empty_ids_and_trims()
    {
        var page = OrcaRouterManagedInferenceProvider.ParseModels("""
            {
              "data": [
                { "id": "" },
                { "id": "   " },
                { "owned_by": "No Id" },
                {
                  "id": " vendor/kept ",
                  "architecture": { "output_modalities": ["text"] }
                }
              ]
            }
            """);

        var model = Assert.Single(page);
        Assert.Equal("vendor/kept", model.Slug);
        Assert.Equal("vendor/kept", model.DisplayName);
        Assert.Empty(model.EffortLevels);
    }

    [Fact]
    public async Task GetModelsAsync_sends_bearer_and_filters_catalog()
    {
        var handler = new StubHttpHandler(_ => Json("""
            {
              "data": [
                {
                  "id": "anthropic/claude-sonnet-4",
                  "architecture": { "output_modalities": ["text"] }
                },
                {
                  "id": "kling/skip-me",
                  "architecture": { "output_modalities": ["text"] }
                }
              ]
            }
            """));

        var provider = CreateProvider(handler);
        var result = await provider.GetModelsAsync(" sk-orca-test ");
        Assert.False(result.IsError, result.IsError ? result.Error : "");
        var model = Assert.Single(result.Value);
        Assert.Equal("anthropic/claude-sonnet-4", model.Slug);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.orcarouter.ai/v1/models", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-orca-test", request.Headers.Authorization?.Parameter);
        Assert.False(request.Headers.Contains("HTTP-Referer"));
        Assert.False(request.Headers.Contains("X-Title"));
    }

    [Fact]
    public async Task GetModelsAsync_errors_on_http_failure_and_invalid_json()
    {
        var unauthorized = CreateProvider(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("nope"),
            }));
        var httpError = await unauthorized.GetModelsAsync("sk-test");
        Assert.True(httpError.IsError);
        Assert.Contains("401", httpError.Error, StringComparison.Ordinal);

        var badJson = CreateProvider(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ not json"),
            }));
        var jsonError = await badJson.GetModelsAsync("sk-test");
        Assert.True(jsonError.IsError);
        Assert.Contains("JSON", jsonError.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetModelsAsync_rejects_empty_api_key_without_http()
    {
        var handler = new FailOnRequestHandler();
        var provider = CreateProvider(handler);
        var result = await provider.GetModelsAsync("  ");
        Assert.True(result.IsError);
        Assert.Contains("API key", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ImportAsync_creates_provider_with_zero_slugs_and_skips_catalog_http()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);
        var handler = new FailOnRequestHandler();
        var provider = new OrcaRouterManagedInferenceProvider(new HttpClient(handler), store);

        var imported = await provider.ImportAsync(" sk-orca-v1-test ");
        Assert.False(imported.IsError, imported.IsError ? imported.Error : "");
        Assert.Equal(0, handler.Calls);

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        var row = Assert.Single(listed.Value, p => p.ManagedSource == DysonManagedSources.OrcaRouter);
        Assert.Equal(imported.Value, row.Id);
        Assert.Equal("OrcaRouter", row.DisplayName);
        Assert.Equal("https://api.orcarouter.ai/v1", row.BaseUrl);
        Assert.Equal("sk-orca-v1-test", row.ApiKey);
        Assert.Equal(DysonOpenAiApiModes.Completions, row.OpenAiApiMode);
        Assert.Equal("OpenAICompatible", row.ProviderKind);
        Assert.Empty(row.Slugs);
    }

    [Fact]
    public async Task ImportAsync_rejects_empty_api_key()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);
        var provider = new OrcaRouterManagedInferenceProvider(new HttpClient(new FailOnRequestHandler()), store);

        var result = await provider.ImportAsync(" \t ");
        Assert.True(result.IsError);

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        Assert.DoesNotContain(listed.Value, p => p.ManagedSource == DysonManagedSources.OrcaRouter);
    }

    [Fact]
    public async Task ImportAsync_second_call_does_not_wipe_slugs()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);
        var provider = new OrcaRouterManagedInferenceProvider(new HttpClient(new FailOnRequestHandler()), store);

        var first = await provider.ImportAsync("first-key");
        Assert.False(first.IsError, first.IsError ? first.Error : "");

        var enable = await store.UpsertManagedSlugAsync(
            first.Value,
            new ManagedSlugSpec("anthropic/claude-sonnet-4", "anthropic/claude-sonnet-4", null, []),
            enabled: true);
        Assert.False(enable.IsError, enable.IsError ? enable.Error : "");

        var second = await provider.ImportAsync("second-key");
        Assert.False(second.IsError, second.IsError ? second.Error : "");
        Assert.Equal(first.Value, second.Value);

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        var row = Assert.Single(listed.Value, p => p.ManagedSource == DysonManagedSources.OrcaRouter);
        Assert.Equal("second-key", row.ApiKey);
        var slug = Assert.Single(row.Slugs);
        Assert.Equal("anthropic/claude-sonnet-4", slug.Slug);
        Assert.True(slug.IsEnabled);
    }

    [Fact]
    public async Task UpdateApiKeyAsync_does_not_wipe_slugs()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);
        var provider = new OrcaRouterManagedInferenceProvider(new HttpClient(new FailOnRequestHandler()), store);

        var seeded = await store.UpsertManagedProviderAsync(
            DysonManagedSources.OrcaRouter,
            "OrcaRouter",
            OrcaRouterManagedInferenceProvider.ApiBaseUrl,
            "old-key",
            DysonOpenAiApiModes.Completions,
            [new ManagedSlugSpec("anthropic/claude-sonnet-4", "anthropic/claude-sonnet-4", null, [])]);
        Assert.False(seeded.IsError, seeded.IsError ? seeded.Error : "");

        var updated = await provider.UpdateApiKeyAsync(" new-key ");
        Assert.False(updated.IsError, updated.IsError ? updated.Error : "");

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        var row = Assert.Single(listed.Value, p => p.ManagedSource == DysonManagedSources.OrcaRouter);
        Assert.Equal("new-key", row.ApiKey);
        var slug = Assert.Single(row.Slugs);
        Assert.Equal("anthropic/claude-sonnet-4", slug.Slug);
        Assert.True(slug.IsEnabled);
    }

    private static ManagedInferenceModel MapOne(string itemJson)
    {
        var page = OrcaRouterManagedInferenceProvider.ParseModels(
            """{ "data": [ """ + itemJson + """ ] }""");
        return Assert.Single(page);
    }

    private static OrcaRouterManagedInferenceProvider CreateProvider(HttpMessageHandler handler)
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        _ = conn;
        return new OrcaRouterManagedInferenceProvider(new HttpClient(handler), DysonTempDb.Models(accessor));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FailOnRequestHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException($"Unexpected HTTP {request.Method} {request.RequestUri}");
        }
    }
}

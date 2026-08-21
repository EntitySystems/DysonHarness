using System.Net;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// OpenRouter catalog parse/map/paginate + Import. No live HTTP.
/// </summary>
public class OpenRouterManagedInferenceProviderTests
{
    private static readonly string[] Gateway =
        ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    [Fact]
    public void Identity_is_openrouter()
    {
        var provider = CreateProvider(new FailOnRequestHandler());
        Assert.Equal("openrouter", provider.ManagedSource);
        Assert.Equal(DysonManagedSources.OpenRouter, provider.ManagedSource);
        Assert.Equal("OpenRouter", provider.DisplayName);
    }

    [Fact]
    public void Map_uses_supported_efforts_array_and_default_effort()
    {
        var model = MapOne("""
            {
              "id": "anthropic/claude-sonnet-4",
              "name": "Claude Sonnet 4",
              "supported_parameters": ["reasoning", "tools"],
              "reasoning": {
                "supported_efforts": ["low", "medium", "high"],
                "default_effort": "high",
                "mandatory": false
              }
            }
            """);

        Assert.Equal("anthropic/claude-sonnet-4", model.Slug);
        Assert.Equal("Claude Sonnet 4", model.DisplayName);
        Assert.Equal(["low", "medium", "high"], model.EffortLevels);
        Assert.Equal("high", model.DefaultReasoningEffort);
    }

    [Fact]
    public void Map_supported_efforts_null_uses_gateway_set()
    {
        var model = MapOne("""
            {
              "id": "openai/o3",
              "name": "o3",
              "reasoning": { "supported_efforts": null }
            }
            """);

        Assert.Equal(Gateway, model.EffortLevels);
        Assert.Equal("high", model.DefaultReasoningEffort);
    }

    [Fact]
    public void Map_reasoning_omitted_with_reasoning_parameter_uses_gateway_set()
    {
        var byReasoning = MapOne("""
            {
              "id": "vendor/reasoner",
              "supported_parameters": ["tools", "reasoning"]
            }
            """);
        Assert.Equal(Gateway, byReasoning.EffortLevels);
        Assert.Equal("high", byReasoning.DefaultReasoningEffort);

        var byEffort = MapOne("""
            {
              "id": "vendor/reasoner-effort",
              "supported_parameters": ["reasoning_effort"]
            }
            """);
        Assert.Equal(Gateway, byEffort.EffortLevels);
    }

    [Fact]
    public void Map_reasoning_omitted_without_parameter_is_empty()
    {
        var model = MapOne("""
            { "id": "vendor/plain", "supported_parameters": ["tools"] }
            """);

        Assert.Empty(model.EffortLevels);
        Assert.Null(model.DefaultReasoningEffort);
        Assert.Equal("vendor/plain", model.DisplayName);
    }

    [Fact]
    public void Map_mandatory_true_strips_none()
    {
        var fromGateway = MapOne("""
            {
              "id": "vendor/mandatory-gateway",
              "reasoning": { "supported_efforts": null, "mandatory": true }
            }
            """);
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh", "max"], fromGateway.EffortLevels);
        Assert.DoesNotContain("none", fromGateway.EffortLevels);
        Assert.Equal("high", fromGateway.DefaultReasoningEffort);

        var fromArray = MapOne("""
            {
              "id": "vendor/mandatory-array",
              "reasoning": {
                "supported_efforts": ["none", "low", "high"],
                "mandatory": true
              }
            }
            """);
        Assert.Equal(["low", "high"], fromArray.EffortLevels);
    }

    [Fact]
    public void Map_default_effort_falls_back_high_then_first_then_null()
    {
        var specified = MapOne("""
            {
              "id": "vendor/specified",
              "reasoning": {
                "supported_efforts": ["low", "medium", "high"],
                "default_effort": "medium"
              }
            }
            """);
        Assert.Equal("medium", specified.DefaultReasoningEffort);

        var highFallback = MapOne("""
            {
              "id": "vendor/high-fallback",
              "reasoning": { "supported_efforts": ["low", "high", "xhigh"] }
            }
            """);
        Assert.Equal("high", highFallback.DefaultReasoningEffort);

        var firstFallback = MapOne("""
            {
              "id": "vendor/first-fallback",
              "reasoning": { "supported_efforts": ["low", "medium"] }
            }
            """);
        Assert.Equal("low", firstFallback.DefaultReasoningEffort);

        var none = MapOne("""{ "id": "vendor/none" }""");
        Assert.Null(none.DefaultReasoningEffort);
        Assert.Empty(none.EffortLevels);
    }

    [Fact]
    public void Parse_skips_empty_ids_and_trims_effort_values()
    {
        var page = OpenRouterManagedInferenceProvider.ParseModelsPage("""
            {
              "data": [
                { "id": "" },
                { "id": "   " },
                { "name": "No Id" },
                {
                  "id": " vendor/kept ",
                  "name": "  Kept  ",
                  "reasoning": { "supported_efforts": ["", " low ", "medium"] }
                }
              ]
            }
            """);

        var model = Assert.Single(page.Models);
        Assert.Equal("vendor/kept", model.Slug);
        Assert.Equal("Kept", model.DisplayName);
        Assert.Equal(["low", "medium"], model.EffortLevels);
    }

    [Fact]
    public void Parse_reads_links_next()
    {
        var page = OpenRouterManagedInferenceProvider.ParseModelsPage("""
            {
              "data": [{ "id": "a/b" }],
              "links": { "next": "https://openrouter.ai/api/v1/models?output_modalities=text&after=x" }
            }
            """);
        Assert.Equal(
            "https://openrouter.ai/api/v1/models?output_modalities=text&after=x",
            page.NextLink);

        var relative = OpenRouterManagedInferenceProvider.ParseModelsPage("""
            { "data": [], "links": { "next": "/api/v1/models?after=y" } }
            """);
        Assert.Equal("/api/v1/models?after=y", relative.NextLink);

        var missing = OpenRouterManagedInferenceProvider.ParseModelsPage("""{ "data": [] }""");
        Assert.Null(missing.NextLink);
    }

    [Fact]
    public void ResolveNextUrl_joins_root_relative_to_https_authority()
    {
        const string current = "https://openrouter.ai/api/v1/models?output_modalities=text";
        Assert.Equal(
            "https://openrouter.ai/api/v1/models?output_modalities=text&after=page2",
            OpenRouterManagedInferenceProvider.ResolveNextUrl(
                current,
                "/api/v1/models?output_modalities=text&after=page2"));
        Assert.Equal(
            "https://openrouter.ai/api/v1/models?after=x",
            OpenRouterManagedInferenceProvider.ResolveNextUrl(
                current,
                "https://openrouter.ai/api/v1/models?after=x"));
    }

    [Fact]
    public async Task GetModelsAsync_follows_two_pages_including_relative_next()
    {
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("after=page2", StringComparison.Ordinal))
            {
                return Json("""
                    { "data": [{ "id": "vendor/two", "name": "Two" }] }
                    """);
            }

            return Json("""
                {
                  "data": [{ "id": "vendor/one", "name": "One" }],
                  "links": { "next": "/api/v1/models?output_modalities=text&after=page2" }
                }
                """);
        });

        var provider = CreateProvider(handler);
        var result = await provider.GetModelsAsync(" sk-test ");
        Assert.False(result.IsError, result.IsError ? result.Error : "");
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(["vendor/one", "vendor/two"], result.Value.Select(m => m.Slug).ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://openrouter.ai/api/v1/models?output_modalities=text",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(
            "https://openrouter.ai/api/v1/models?output_modalities=text&after=page2",
            handler.Requests[1].RequestUri!.ToString());
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal("sk-test", r.Headers.Authorization?.Parameter);
            Assert.False(r.Headers.Contains("HTTP-Referer"));
            Assert.False(r.Headers.Contains("X-Title"));
        });
    }

    [Fact]
    public async Task GetModelsAsync_stops_at_page_ceiling()
    {
        var handler = new StubHttpHandler(_ => Json("""
            {
              "data": [{ "id": "vendor/loop" }],
              "links": { "next": "https://openrouter.ai/api/v1/models?output_modalities=text&after=more" }
            }
            """));

        var provider = CreateProvider(handler);
        var result = await provider.GetModelsAsync("sk-test");
        Assert.False(result.IsError, result.IsError ? result.Error : "");
        Assert.Equal(OpenRouterManagedInferenceProvider.MaxCatalogPages, handler.Requests.Count);
        Assert.Equal(OpenRouterManagedInferenceProvider.MaxCatalogPages, result.Value.Count);
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
        var provider = new OpenRouterManagedInferenceProvider(new HttpClient(handler), store);

        var imported = await provider.ImportAsync(" sk-or-v1-test ");
        Assert.False(imported.IsError, imported.IsError ? imported.Error : "");
        Assert.Equal(0, handler.Calls);

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        var row = Assert.Single(listed.Value, p => p.ManagedSource == DysonManagedSources.OpenRouter);
        Assert.Equal(imported.Value, row.Id);
        Assert.Equal("OpenRouter", row.DisplayName);
        Assert.Equal("https://openrouter.ai/api/v1", row.BaseUrl);
        Assert.Equal("sk-or-v1-test", row.ApiKey);
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
        var provider = new OpenRouterManagedInferenceProvider(new HttpClient(new FailOnRequestHandler()), store);

        var result = await provider.ImportAsync(" \t ");
        Assert.True(result.IsError);

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        Assert.DoesNotContain(listed.Value, p => p.ManagedSource == DysonManagedSources.OpenRouter);
    }

    [Fact]
    public async Task ImportAsync_second_call_does_not_wipe_slugs()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Models(accessor);
        var provider = new OpenRouterManagedInferenceProvider(new HttpClient(new FailOnRequestHandler()), store);

        var first = await provider.ImportAsync("first-key");
        Assert.False(first.IsError, first.IsError ? first.Error : "");

        var enable = await store.UpsertManagedSlugAsync(
            first.Value,
            new ManagedSlugSpec("anthropic/claude-sonnet-4", "Claude Sonnet 4", "high", ["high"]),
            enabled: true);
        Assert.False(enable.IsError, enable.IsError ? enable.Error : "");

        var second = await provider.ImportAsync("second-key");
        Assert.False(second.IsError, second.IsError ? second.Error : "");
        Assert.Equal(first.Value, second.Value);

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        var row = Assert.Single(listed.Value, p => p.ManagedSource == DysonManagedSources.OpenRouter);
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
        var provider = new OpenRouterManagedInferenceProvider(new HttpClient(new FailOnRequestHandler()), store);

        var seeded = await store.UpsertManagedProviderAsync(
            DysonManagedSources.OpenRouter,
            "OpenRouter",
            OpenRouterManagedInferenceProvider.ApiBaseUrl,
            "old-key",
            DysonOpenAiApiModes.Completions,
            [new ManagedSlugSpec("anthropic/claude-sonnet-4", "Claude Sonnet 4", "high", ["high"])]);
        Assert.False(seeded.IsError, seeded.IsError ? seeded.Error : "");

        var updated = await provider.UpdateApiKeyAsync(" new-key ");
        Assert.False(updated.IsError, updated.IsError ? updated.Error : "");

        var listed = await store.ListProvidersAsync();
        Assert.False(listed.IsError, listed.IsError ? listed.Error : "");
        var row = Assert.Single(listed.Value, p => p.ManagedSource == DysonManagedSources.OpenRouter);
        Assert.Equal("new-key", row.ApiKey);
        var slug = Assert.Single(row.Slugs);
        Assert.Equal("anthropic/claude-sonnet-4", slug.Slug);
        Assert.True(slug.IsEnabled);
    }

    private static ManagedInferenceModel MapOne(string itemJson)
    {
        var page = OpenRouterManagedInferenceProvider.ParseModelsPage(
            """{ "data": [ """ + itemJson + """ ] }""");
        return Assert.Single(page.Models);
    }

    private static OpenRouterManagedInferenceProvider CreateProvider(HttpMessageHandler handler)
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        // Keep the in-memory SQLite connection alive for the provider's lifetime in HTTP-only tests.
        _ = conn;
        return new OpenRouterManagedInferenceProvider(new HttpClient(handler), DysonTempDb.Models(accessor));
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

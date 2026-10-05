using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

public class XaiEncryptedContentValidatorTests
{
    private static readonly string Valid = new('A', 64);

    [Fact]
    public void Accepts_plausible_unpadded_base64()
    {
        Assert.True(XaiEncryptedContentValidator.Inspect(Valid).IsSuccess);
        Assert.True(XaiEncryptedContentValidator.Inspect(new string('a', 40) + "-_" + new string('B', 30)).IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_empty(string? value) =>
        Assert.True(XaiEncryptedContentValidator.Inspect(value).IsError);

    [Fact]
    public void Rejects_padding_whitespace_codex_short_charset_and_oversize()
    {
        Assert.Contains("padded", XaiEncryptedContentValidator.Inspect(Valid[..60] + "==").Error);
        Assert.Contains("whitespace", XaiEncryptedContentValidator.Inspect(" " + Valid).Error);
        Assert.Contains("whitespace", XaiEncryptedContentValidator.Inspect(Valid + "\n").Error);
        Assert.Contains("codex", XaiEncryptedContentValidator.Inspect("gAAAA" + Valid).Error);
        Assert.Contains("short", XaiEncryptedContentValidator.Inspect(new string('A', 20)).Error);
        Assert.Contains("base64", XaiEncryptedContentValidator.Inspect(Valid + "!").Error);
        Assert.Contains("large", XaiEncryptedContentValidator.Inspect(
            new string('A', XaiEncryptedContentValidator.MaxEncodedLength + 1)).Error);
    }
}

public class XaiResponsesRequestSanitizerTests
{
    private static readonly string Valid = new('A', 64);

    private static JsonObject Body(JsonArray? input = null, string? effort = null, int tools = 1)
    {
        var body = new JsonObject
        {
            ["model"] = "grok-4.7",
            ["input"] = input ?? [],
            ["tools"] = new JsonArray(Enumerable.Range(0, tools)
                .Select(i => (JsonNode?)new JsonObject { ["type"] = "function", ["name"] = $"t{i}" }).ToArray()),
            ["prompt_cache_key"] = "dyson:x:sp0",
            ["store"] = false,
            ["stream"] = true,
            ["previous_response_id"] = "resp_1",
            ["prompt_cache_retention"] = "24h",
            ["safety_identifier"] = "s",
            ["stream_options"] = new JsonObject(),
            ["stop"] = "x",
        };
        if (effort is not null)
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        return body;
    }

    [Fact]
    public void Drops_unsupported_fields_and_keeps_prompt_cache_key()
    {
        var body = Body();

        XaiResponsesRequestSanitizer.Apply(body, XaiGrokModelCatalog.Find("grok-4.7"));

        foreach (var gone in new[] { "previous_response_id", "prompt_cache_retention", "safety_identifier", "stream_options", "stop" })
            Assert.False(body.ContainsKey(gone), gone);
        Assert.Equal("dyson:x:sp0", body["prompt_cache_key"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("grok-4.7", body["model"]!.GetValue<string>());
    }

    [Fact]
    public void Clamps_tools_to_200()
    {
        var body = Body(tools: 250);

        XaiResponsesRequestSanitizer.Apply(body, null);

        var tools = (JsonArray)body["tools"]!;
        Assert.Equal(200, tools.Count);
        Assert.Equal("t199", tools[^1]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("grok-4.7", "none", null)]
    [InlineData("grok-4.7", "minimal", "low")]
    [InlineData("grok-4.7", "xhigh", "xhigh")]
    [InlineData("grok-4.7", "high", "high")]
    [InlineData("grok-4.5", "xhigh", "high")]
    [InlineData("grok-4.3", "none", "none")]
    [InlineData("grok-4.3", "xhigh", "high")]
    [InlineData("grok-3-mini", "medium", "medium")]
    [InlineData("grok-build-0.1", "high", null)]
    [InlineData("grok-4.20-0309-reasoning", "high", null)]
    public void Reasoning_effort_is_clamped_to_the_models_levels(string model, string effort, string? expected)
    {
        var body = Body(effort: effort);

        XaiResponsesRequestSanitizer.Apply(body, XaiGrokModelCatalog.Find(model));

        if (expected is null)
            Assert.False(body.ContainsKey("reasoning"));
        else
            Assert.Equal(expected, body["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_model_leaves_effort_alone()
    {
        var body = Body(effort: "xhigh");

        XaiResponsesRequestSanitizer.Apply(body, null);

        Assert.Equal("xhigh", body["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Strips_null_content_and_invalid_encrypted_content_from_reasoning_items()
    {
        var input = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "rs_1", ["content"] = null,
                ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "a" }),
                ["encrypted_content"] = Valid,
            },
            new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "hi" },
            new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "rs_2",
                ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "b" }),
                ["encrypted_content"] = "gAAAA" + Valid,
            },
            new JsonObject { ["type"] = "reasoning", ["id"] = "rs_3", ["summary"] = new JsonArray(), ["encrypted_content"] = null },
        };
        var body = Body(input);

        XaiResponsesRequestSanitizer.Apply(body, null);

        var result = (JsonArray)body["input"]!;
        var first = (JsonObject)result[0]!;
        Assert.False(first.ContainsKey("content"));
        Assert.Equal(Valid, first["encrypted_content"]!.GetValue<string>());
        var second = result.OfType<JsonObject>().Single(i => i["id"]?.GetValue<string>() == "rs_2");
        Assert.False(second.ContainsKey("encrypted_content"));
        Assert.Single((JsonArray)second["summary"]!);
        Assert.Equal("hi", result[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Merges_adjacent_summary_only_reasoning_items_and_drops_invalid_compaction()
    {
        var input = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "rs_a",
                ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "one" }),
            },
            new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "rs_b",
                ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "two" }),
            },
            new JsonObject { ["type"] = "compaction", ["encrypted_content"] = "bad" },
            new JsonObject { ["type"] = "compaction", ["encrypted_content"] = Valid },
        };
        var body = Body(input);

        XaiResponsesRequestSanitizer.Apply(body, null);

        var result = (JsonArray)body["input"]!;
        Assert.Equal(2, result.Count);
        Assert.Equal("reasoning", result[0]!["type"]!.GetValue<string>());
        var texts = ((JsonArray)result[0]!["summary"]!).Select(p => p!["text"]!.GetValue<string>()).ToArray();
        Assert.Equal(["one", "two"], texts);
        Assert.Equal("compaction", result[1]!["type"]!.GetValue<string>());
        Assert.Equal(Valid, result[1]!["encrypted_content"]!.GetValue<string>());
    }

    private static OpenAiCompatibleAgentProvider MakeProvider(string? managedSource, string slug, string? effort)
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "P",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://example.invalid/v1",
            ApiKey = "k",
            OpenAiApiMode = DysonOpenAiApiModes.Responses,
            ManagedSource = managedSource,
        };
        var slugEntity = new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(), ProviderId = entity.Id, Slug = slug, DisplayAlias = slug, Provider = entity,
        };
        return new OpenAiCompatibleAgentProvider(entity, slugEntity, effort);
    }

    private static OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest Built(string? previousId) =>
        new("sys", [], [], "dyson:k:sp0", false, previousId, false);

    [Fact]
    public void BuildCreateBody_shapes_only_xai_grok_providers()
    {
        var xai = MakeProvider(DysonManagedSources.XaiGrok, "grok-4.5", "xhigh");
        var codex = MakeProvider(DysonManagedSources.CliProxyCodex, "grok-4.5", "xhigh");
        var direct = MakeProvider(null, "gpt-5", "xhigh");

        var xaiBody = OpenAiResponsesClient.BuildCreateBody(xai, Built("resp_1"));
        var codexBody = OpenAiResponsesClient.BuildCreateBody(codex, Built("resp_1"));
        var directBody = OpenAiResponsesClient.BuildCreateBody(direct, Built("resp_1"));

        Assert.Equal("high", xaiBody["reasoning"]!["effort"]!.GetValue<string>());
        Assert.False(xaiBody.ContainsKey("previous_response_id"));
        Assert.Equal("xhigh", codexBody["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("resp_1", codexBody["previous_response_id"]!.GetValue<string>());
        Assert.Equal("resp_1", directBody["previous_response_id"]!.GetValue<string>());
    }
}

public class XaiGrokModelCatalogTests
{
    [Fact]
    public void Bundled_table_has_the_chat_models_and_no_imagine_ids()
    {
        var ids = XaiGrokModelCatalog.Bundled.Select(m => m.Id).ToArray();

        Assert.Contains("grok-4.7", ids);
        Assert.Contains("grok-4.3", ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(ids, id => id.StartsWith("grok-imagine-", StringComparison.Ordinal));
        Assert.Equal(["low", "medium", "high", "xhigh"], XaiGrokModelCatalog.Find("grok-4.7")!.ThinkingLevels);
        Assert.Equal(["none", "low", "medium", "high"], XaiGrokModelCatalog.Find("grok-4.3")!.ThinkingLevels);
        Assert.Empty(XaiGrokModelCatalog.Find("grok-build-0.1")!.ThinkingLevels);
    }

    [Fact]
    public void Live_list_parse_excludes_imagine_and_keeps_known_levels()
    {
        var parsed = XaiGrokModelCatalog.ParseLiveModels(
            """{"data":[{"id":"grok-4.7"},{"id":"grok-imagine-image-2.0"},{"id":"grok-imagine-video-1.5"},{"id":"grok-9-new"},{"id":"grok-4.7"}]}""");

        Assert.True(parsed.IsSuccess);
        Assert.Equal(["grok-4.7", "grok-9-new"], parsed.Value.Select(m => m.Id).ToArray());
        Assert.Equal(4, parsed.Value[0].ThinkingLevels.Count);
        Assert.Empty(parsed.Value[1].ThinkingLevels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"models":[]}""")]
    [InlineData("""{"data":[{"id":"grok-imagine-image-2.0"}]}""")]
    public void Live_list_failures_are_error_results(string json) =>
        Assert.True(XaiGrokModelCatalog.ParseLiveModels(json).IsError);

    [Fact]
    public void Slug_spec_uses_real_levels_and_default_effort()
    {
        var g47 = XaiGrokModelCatalog.ToSlugSpec(XaiGrokModelCatalog.Find("grok-4.7")!);
        Assert.Equal("high", g47.DefaultReasoningEffort);
        Assert.Equal(["low", "medium", "high", "xhigh"], g47.ReasoningModes);

        var g43 = XaiGrokModelCatalog.ToSlugSpec(XaiGrokModelCatalog.Find("grok-4.3")!);
        Assert.Equal("high", g43.DefaultReasoningEffort);
        Assert.Contains("none", g43.ReasoningModes);

        var build = XaiGrokModelCatalog.ToSlugSpec(XaiGrokModelCatalog.Find("grok-build-0.1")!);
        Assert.Null(build.DefaultReasoningEffort);
        Assert.Empty(build.ReasoningModes);

        var mid = XaiGrokModelCatalog.DefaultEffort(new XaiModelInfo("x", "x", null, ["low", "medium"]));
        Assert.Equal("medium", mid);
    }
}

public class XaiGrokProviderWiringTests
{
    [Fact]
    public void Managed_source_is_neither_cliproxy_nor_direct_managed()
    {
        Assert.True(DysonManagedSources.IsXaiGrok("xai-grok"));
        Assert.False(DysonManagedSources.IsXaiGrok("cliproxy-grok"));
        Assert.False(DysonManagedSources.IsCliProxy("xai-grok"));
        Assert.False(DysonManagedSources.IsDirectManaged("xai-grok"));
        Assert.True(DysonManagedSources.IsCliProxy(DysonManagedSources.CliProxyGrok));
    }

    [Fact]
    public void Agent_provider_rewrites_base_url_for_xai_grok_but_keeps_the_handle_and_cliproxy_constants_alone()
    {
        var handle = XaiGrokAuthService.HandleFor(Guid.NewGuid());
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Grok Build (xAI)",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://tampered.example/v1",
            ApiKey = handle,
            OpenAiApiMode = DysonOpenAiApiModes.Responses,
            ManagedSource = DysonManagedSources.XaiGrok,
        };
        var slug = new DysonModelSlugEntity { Id = Guid.NewGuid(), ProviderId = entity.Id, Slug = "grok-4.7", Provider = entity };

        var provider = new OpenAiCompatibleAgentProvider(entity, slug);

        Assert.Equal(XaiGrokClientProfile.ChatProxyBaseUrl, provider.BaseUrl);
        Assert.Equal(handle, provider.ApiKey);
        Assert.NotEqual(DysonCliProxyHost.DefaultLocalBaseUrl, provider.BaseUrl);
        Assert.NotEqual(DysonCliProxyHost.DefaultApiKey, provider.ApiKey);
        Assert.False(OpenAiCompatibleHttp.SupportsResponsesServerChaining(provider));
        Assert.False(OpenAiCompatibleHttp.SupportsExplicitPromptCache(provider));
    }
}

public class XaiStreamCompatibilityTests
{
    private const string Sse = """
        data: {"type":"response.created","response":{"id":"resp_1"}}

        data: {"type":"keepalive"}

        data: {"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}

        data: {"type":"response.reasoning_text.delta","delta":"think"}

        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}}

        data: {"type":"response.output_text.delta","delta":"Hel"}

        data: {"type":"response.output_text.delta","delta":"lo"}

        data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"GetDateTime","arguments":""}}

        data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":1,"delta":"{\"stage\":0}"}

        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"GetDateTime","arguments":"{\"stage\":0}"}}

        data: {"type":"response.completed","response":{"id":"resp_1","output":[],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}

        """;

    private static async Task<OpenAiModelReply> RunAsync(string? managedSource)
    {
        var handler = new FakeXaiHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream"),
        });
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "P",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = XaiGrokClientProfile.ChatProxyBaseUrl,
            ApiKey = "k",
            OpenAiApiMode = DysonOpenAiApiModes.Responses,
            ManagedSource = managedSource,
        };
        var slug = new DysonModelSlugEntity { Id = Guid.NewGuid(), ProviderId = entity.Id, Slug = "grok-4.7", Provider = entity };
        var provider = new OpenAiCompatibleAgentProvider(entity, slug);
        var built = new OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest("sys", [], [], "dyson:k:sp0", false, null, false);

        var client = new OpenAiResponsesClient(new HttpClient(handler));
        OpenAiModelReply? reply = null;
        var deltas = new StringBuilder();
        await foreach (var chunk in client.StreamCreateAsync(provider, built))
        {
            Assert.True(chunk.IsSuccess, chunk.IsError ? chunk.Error : null);
            deltas.Append(chunk.Value.TextDelta);
            if (chunk.Value.CompletedReply is not null)
                reply = chunk.Value.CompletedReply;
        }

        Assert.Equal("Hello", deltas.ToString());
        return reply!;
    }

    [Fact]
    public async Task Xai_stream_with_empty_completed_output_still_yields_text_reasoning_tools_usage_and_reasoning_items()
    {
        var reply = await RunAsync(DysonManagedSources.XaiGrok);

        Assert.Equal("Hello", reply.Content);
        Assert.Equal("think", reply.ReasoningContent);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("GetDateTime", call.ToolName);
        Assert.NotNull(reply.Usage);
        // Rebuilt from output_item.done because completed.output was empty.
        var item = Assert.Single(reply.ReasoningOutputItems);
        Assert.Equal("rs_1", item["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Other_providers_keep_todays_behaviour_for_an_empty_completed_output()
    {
        var reply = await RunAsync(DysonManagedSources.CliProxyCodex);

        Assert.Equal("Hello", reply.Content);
        Assert.Single(reply.ToolCalls);
        Assert.Empty(reply.ReasoningOutputItems);
    }
}

using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert Completions/Responses reasoning parse, BaseUrl normalize, turn preview handoff
/// (Xunit Fact). /// </summary>
public class OpenAiReasoningTests
{
    [Fact]
    public void Run()
    {
        AssertNormalizeBaseUrl();
        AssertCompletionsParseReasoning();
        AssertResponsesParseReasoning();
        AssertResponsesCreateBodyNestedReasoningEffort();
        AssertCompletionsCreateBodyReasoningEffort();
        AssertPromptCacheOptionsGate();
        AssertTurnReasoningPreviewHandoff();
        AssertNullEffortFallsBackToSlugDefault();
        AssertExplicitNoneKeepsLiteralNone();
    }

    private static void AssertNullEffortFallsBackToSlugDefault()
    {
        var slug = new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Slug = "gpt-test",
            DisplayAlias = "gpt-test",
            DefaultReasoningEffort = "medium",
        };
        var provider = new OpenAiCompatibleAgentProvider(
            provider: null,
            slug: slug,
            reasoningEffort: null);
        if (!string.Equals(provider.ReasoningEffort, "medium", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"null reasoningEffort must use slug DefaultReasoningEffort 'medium', got '{provider.ReasoningEffort ?? "null"}'.");
        }
    }

    private static void AssertExplicitNoneKeepsLiteralNone()
    {
        var slug = new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Slug = "gpt-test",
            DisplayAlias = "gpt-test",
            DefaultReasoningEffort = "medium",
        };
        var provider = new OpenAiCompatibleAgentProvider(
            provider: null,
            slug: slug,
            reasoningEffort: "none");
        if (!string.Equals(provider.ReasoningEffort, "none", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"explicit reasoningEffort 'none' must not fall back to slug default, got '{provider.ReasoningEffort ?? "null"}'.");
        }
    }

    private static void AssertNormalizeBaseUrl()
    {
        static void Expect(string? input, string expectedRoot)
        {
            var actual = OpenAiCompatibleHttp.NormalizeBaseUrl(input);
            if (!string.Equals(actual, expectedRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"NormalizeBaseUrl({input ?? "null"}) => '{actual}', expected '{expectedRoot}'.");
            }
        }

        Expect("https://api.openai.com", "https://api.openai.com/v1");
        Expect("https://api.z.ai/api/paas/v4/", "https://api.z.ai/api/paas/v4");
        Expect("https://api.openai.com/v1", "https://api.openai.com/v1");

        var openaiChat = $"{OpenAiCompatibleHttp.NormalizeBaseUrl("https://api.openai.com")}/chat/completions";
        if (!string.Equals(openaiChat, "https://api.openai.com/v1/chat/completions", StringComparison.Ordinal))
            throw new InvalidOperationException($"OpenAI chat URL was '{openaiChat}'.");

        var zaiChat = $"{OpenAiCompatibleHttp.NormalizeBaseUrl("https://api.z.ai/api/paas/v4/")}/chat/completions";
        if (!string.Equals(zaiChat, "https://api.z.ai/api/paas/v4/chat/completions", StringComparison.Ordinal))
            throw new InvalidOperationException($"Z.AI chat URL was '{zaiChat}' (must not insert /v1).");

        var zaiResponses = $"{OpenAiCompatibleHttp.NormalizeBaseUrl("https://api.z.ai/api/paas/v4/")}/responses";
        if (!string.Equals(zaiResponses, "https://api.z.ai/api/paas/v4/responses", StringComparison.Ordinal))
            throw new InvalidOperationException($"Z.AI responses URL was '{zaiResponses}'.");
    }

    private static void AssertCompletionsParseReasoning()
    {
        var json = JsonNode.Parse("""
            {
              "id": "chatcmpl-test",
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": "# Title\n\nBody",
                  "reasoning_content": "step one"
                }
              }]
            }
            """) as JsonObject
            ?? throw new InvalidOperationException("Expected Completions fixture JsonObject.");

        var parsed = OpenAiCompletionsClient.Parse(json);
        if (parsed.IsError)
            throw new InvalidOperationException($"Completions Parse failed: {parsed.Error}");

        if (!string.Equals(parsed.Value.ReasoningContent, "step one", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected reasoning_content 'step one', got '{parsed.Value.ReasoningContent}'.");
        }
    }

    private static void AssertResponsesParseReasoning()
    {
        var json = JsonNode.Parse("""
            {
              "id": "resp-test",
              "output": [
                {
                  "type": "reasoning",
                  "summary": [{ "type": "summary_text", "text": "think A" }],
                  "content": [{ "type": "reasoning_text", "text": "think B" }]
                },
                {
                  "type": "message",
                  "content": [{ "type": "output_text", "text": "# Hi\n\nDone" }]
                }
              ]
            }
            """) as JsonObject
            ?? throw new InvalidOperationException("Expected Responses fixture JsonObject.");

        var parsed = OpenAiResponsesClient.Parse(json);
        if (parsed.IsError)
            throw new InvalidOperationException($"Responses Parse failed: {parsed.Error}");

        var expected = "think A\nthink B";
        if (!string.Equals(parsed.Value.ReasoningContent, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected ReasoningContent '{expected}', got '{parsed.Value.ReasoningContent}'.");
        }
    }

    private static void AssertResponsesCreateBodyNestedReasoningEffort()
    {
        var provider = new OpenAiCompatibleAgentProvider(
            provider: null,
            slug: null,
            reasoningEffort: "high");
        var built = new OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest(
            Instructions: "sys",
            Input: [],
            Tools: [],
            PromptCacheKey: "cache-key",
            IncludeExplicitBreakpoints: false,
            PreviousResponseId: null,
            Store: false);

        var body = OpenAiResponsesClient.BuildCreateBody(provider, built);

        if (body.ContainsKey("reasoning_effort"))
            throw new InvalidOperationException("Responses body must not include top-level reasoning_effort.");

        var effort = body["reasoning"]?["effort"]?.GetValue<string>();
        if (!string.Equals(effort, "high", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected reasoning.effort 'high', got '{effort ?? "null"}'.");
        }
    }

    private static void AssertCompletionsCreateBodyReasoningEffort()
    {
        static OpenAiCompatibleAgentProvider MakeProvider(string? managedSource, string? reasoningEffort)
        {
            var entity = new DysonModelProviderEntity
            {
                Id = Guid.NewGuid(),
                DisplayName = managedSource is null ? "Direct OpenAI" : "Managed",
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = "https://example.test/v1",
                ApiKey = "sk-test",
                OpenAiApiMode = DysonOpenAiApiModes.Completions,
                ManagedSource = managedSource,
            };
            var slugEntity = new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = "test-model",
                DisplayAlias = "test-model",
                Provider = entity,
            };
            return new OpenAiCompatibleAgentProvider(entity, slugEntity, reasoningEffort);
        }

        static JsonObject BodyFor(OpenAiCompatibleAgentProvider provider)
        {
            var body = new JsonObject();
            OpenAiCompletionsClient.ApplyReasoningEffort(body, provider);
            return body;
        }

        static void ExpectNested(JsonObject body, string expected)
        {
            if (body.ContainsKey("reasoning_effort"))
                throw new InvalidOperationException("OpenRouter Completions body must not include top-level reasoning_effort.");

            var actual = body["reasoning"]?["effort"]?.GetValue<string>();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected reasoning.effort '{expected}', got '{actual ?? "null"}'.");
            }
        }

        static void ExpectTopLevel(JsonObject body, string expected)
        {
            if (body.ContainsKey("reasoning"))
                throw new InvalidOperationException("Non-OpenRouter Completions body must not include nested reasoning.");

            var actual = body["reasoning_effort"]?.GetValue<string>();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected reasoning_effort '{expected}', got '{actual ?? "null"}'.");
            }
        }

        static void ExpectOmitted(JsonObject body)
        {
            if (body.ContainsKey("reasoning") || body.ContainsKey("reasoning_effort"))
                throw new InvalidOperationException("Blank/null effort must omit both Completions reasoning shapes.");
        }

        ExpectNested(BodyFor(MakeProvider(DysonManagedSources.OpenRouter, "high")), "high");
        ExpectTopLevel(BodyFor(MakeProvider(managedSource: null, "high")), "high");
        ExpectTopLevel(BodyFor(MakeProvider(DysonManagedSources.CliProxyCodex, "high")), "high");
        ExpectTopLevel(BodyFor(MakeProvider(DysonManagedSources.OrcaRouter, "high")), "high");
        ExpectOmitted(BodyFor(MakeProvider(DysonManagedSources.OpenRouter, null)));
        ExpectOmitted(BodyFor(MakeProvider(DysonManagedSources.OpenRouter, "  ")));
        ExpectNested(BodyFor(MakeProvider(DysonManagedSources.OpenRouter, "none")), "none");
    }

    private static void AssertPromptCacheOptionsGate()
    {
        static OpenAiCompatibleAgentProvider MakeProvider(string slug, string? managedSource)
        {
            var entity = new DysonModelProviderEntity
            {
                Id = Guid.NewGuid(),
                DisplayName = managedSource is null ? "Direct OpenAI" : "Managed Codex",
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = managedSource is null ? "https://api.openai.com/v1" : "http://127.0.0.1:8317/v1",
                OpenAiApiMode = DysonOpenAiApiModes.Responses,
                ManagedSource = managedSource,
            };
            var slugEntity = new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = slug,
                DisplayAlias = slug,
                Provider = entity,
            };
            return new OpenAiCompatibleAgentProvider(entity, slugEntity);
        }

        static JsonObject BodyFor(OpenAiCompatibleAgentProvider provider)
        {
            var built = new OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest(
                Instructions: "sys",
                Input: [],
                Tools: [],
                PromptCacheKey: "dyson:test",
                IncludeExplicitBreakpoints: OpenAiCompatibleHttp.SupportsExplicitPromptCache(provider),
                PreviousResponseId: null,
                Store: false);
            return OpenAiResponsesClient.BuildCreateBody(provider, built);
        }

        var managed = MakeProvider("gpt-5.6-sol", DysonManagedSources.CliProxyCodex);
        if (OpenAiCompatibleHttp.SupportsExplicitPromptCache(managed))
            throw new InvalidOperationException("Managed GPT-5.6 must not support explicit prompt cache.");

        var managedBody = BodyFor(managed);
        if (managedBody.ContainsKey("prompt_cache_options"))
            throw new InvalidOperationException("Managed Responses body must omit prompt_cache_options.");
        if (managedBody["prompt_cache_key"]?.GetValue<string>() is not { Length: > 0 })
            throw new InvalidOperationException("Managed Responses body must still include prompt_cache_key.");

        var direct = MakeProvider("gpt-5.6-sol", managedSource: null);
        if (!OpenAiCompatibleHttp.SupportsExplicitPromptCache(direct))
            throw new InvalidOperationException("Direct GPT-5.6 must support explicit prompt cache.");

        var directBody = BodyFor(direct);
        var mode = directBody["prompt_cache_options"]?["mode"]?.GetValue<string>();
        if (!string.Equals(mode, "explicit", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Direct GPT-5.6 Responses body expected prompt_cache_options.mode=explicit, got '{mode ?? "null"}'.");
        }
    }

    private static void AssertTurnReasoningPreviewHandoff()
    {
        var turn = new DysonAgentTurn();
        turn.AppendReasoningDelta("alpha");
        turn.AppendReasoningDelta(" beta");
        if (!turn.IsReasoningStreaming
            || !string.Equals(turn.ReasoningStreamingPreview, "alpha beta", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reasoning preview did not accumulate deltas.");
        }

        turn.ReasoningText = turn.ReasoningStreamingPreview;
        turn.FinishReasoningStreaming();
        if (turn.IsReasoningStreaming || turn.ReasoningStreamingPreview is not null)
            throw new InvalidOperationException("FinishReasoningStreaming should clear preview flags.");

        if (!string.Equals(turn.ReasoningText, "alpha beta", StringComparison.Ordinal))
            throw new InvalidOperationException("ReasoningText should remain after finish.");
    }
}

using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: Completions/Responses/Anthropic usage parse + after-cache math.
/// </summary>
public class OpenAiUsageParseTests
{
    [Fact]
    public void Completions_usage_maps_prompt_and_completion_tokens()
    {
        var json = Parse(
            """
            { "usage": { "prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120 } }
            """);

        Assert.True(OpenAiCompatibleHttp.TryParseUsage(json, out var usage));
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(20, usage.WriteTokens);
        Assert.Equal(0, usage.CacheTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(100, usage.InputTokensAfterCache);
        Assert.Equal(20, usage.WriteTokensAfterCache);
    }

    [Fact]
    public void Responses_usage_maps_input_and_output_tokens()
    {
        var json = Parse(
            """
            { "usage": { "input_tokens": 80, "output_tokens": 12 } }
            """);

        Assert.True(OpenAiCompatibleHttp.TryParseUsage(json, out var usage));
        Assert.Equal(80, usage.InputTokens);
        Assert.Equal(12, usage.WriteTokens);
        Assert.Equal(80, usage.InputTokensAfterCache);
        Assert.Equal(12, usage.WriteTokensAfterCache);
    }

    [Fact]
    public void Cache_read_and_write_from_prompt_details()
    {
        var json = Parse(
            """
            {
              "usage": {
                "prompt_tokens": 100,
                "completion_tokens": 10,
                "prompt_tokens_details": { "cached_tokens": 40, "cache_write_tokens": 15 }
              }
            }
            """);

        Assert.True(OpenAiCompatibleHttp.TryParseUsage(json, out var usage));
        Assert.Equal(40, usage.CacheTokens);
        Assert.Equal(15, usage.CacheWriteTokens);
        Assert.Equal(60, usage.InputTokensAfterCache);
    }

    [Fact]
    public void Anthropic_cache_read_and_creation_aliases()
    {
        var json = Parse(
            """
            {
              "usage": {
                "input_tokens": 200,
                "output_tokens": 30,
                "cache_read_input_tokens": 50,
                "cache_creation_input_tokens": 25
              }
            }
            """);

        Assert.True(OpenAiCompatibleHttp.TryParseUsage(json, out var usage));
        Assert.Equal(200, usage.InputTokens);
        Assert.Equal(50, usage.CacheTokens);
        Assert.Equal(25, usage.CacheWriteTokens);
        Assert.Equal(30, usage.WriteTokens);
        Assert.Equal(150, usage.InputTokensAfterCache);
        Assert.Equal(30, usage.WriteTokensAfterCache);
    }

    [Fact]
    public void After_cache_floors_at_zero_when_cache_exceeds_input()
    {
        var json = Parse(
            """
            { "usage": { "prompt_tokens": 10, "completion_tokens": 2, "cached_tokens": 50 } }
            """);

        Assert.True(OpenAiCompatibleHttp.TryParseUsage(json, out var usage));
        Assert.Equal(0, usage.InputTokensAfterCache);
    }

    [Fact]
    public void Output_cache_reduces_write_after_cache()
    {
        var json = Parse(
            """
            {
              "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 40,
                "completion_tokens_details": { "cached_tokens": 12 }
              }
            }
            """);

        Assert.True(OpenAiCompatibleHttp.TryParseUsage(json, out var usage));
        Assert.Equal(40, usage.WriteTokens);
        Assert.Equal(28, usage.WriteTokensAfterCache);
    }

    [Fact]
    public void Missing_usage_returns_false()
    {
        var json = Parse("""{ "id": "chatcmpl-1" }""");
        Assert.False(OpenAiCompatibleHttp.TryParseUsage(json, out var usage));
        Assert.Equal(0, usage.InputTokens);
        Assert.False(OpenAiCompatibleHttp.TryParseUsage(null, out _));
    }

    [Fact]
    public void Completions_parse_fills_reply_usage()
    {
        var json = Parse(
            """
            {
              "id": "chatcmpl-1",
              "choices": [{ "message": { "role": "assistant", "content": "hi" } }],
              "usage": { "prompt_tokens": 7, "completion_tokens": 3 }
            }
            """);

        var parsed = OpenAiCompletionsClient.Parse(json);
        Assert.False(parsed.IsError);
        Assert.NotNull(parsed.Value.Usage);
        Assert.Equal(7, parsed.Value.Usage.InputTokens);
        Assert.Equal(3, parsed.Value.Usage.WriteTokens);
    }

    private static JsonObject Parse(string json) =>
        JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidOperationException("Expected a JSON object.");
}

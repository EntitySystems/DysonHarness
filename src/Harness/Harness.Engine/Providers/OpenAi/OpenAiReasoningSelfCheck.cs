using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// ponytail: assert Completions/Responses reasoning parse, BaseUrl normalize, turn preview handoff
/// (no test framework). Run: <c>OpenAiReasoningSelfCheck.Run()</c> (also from UI <c>Program</c> startup).
/// </summary>
public static class OpenAiReasoningSelfCheck
{
    public static void Run()
    {
        AssertNormalizeBaseUrl();
        AssertCompletionsParseReasoning();
        AssertResponsesParseReasoning();
        AssertTurnReasoningPreviewHandoff();
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

// Derived from router-for-me/CLIProxyAPI (MIT) internal/runtime/executor/xai_executor_request.go
//   (prepareResponsesRequestTo subset, sanitizeXAIInputEncryptedContent) @ 97f244b8ddb9cbf564b6e6faab0159102cca8617.
// See THIRD-PARTY-NOTICES.md.

using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// xAI-specific shaping of a POST /responses body that Dyson already built as Responses JSON. Mutates in place.
/// </summary>
public static class XaiResponsesRequestSanitizer
{
    /// <summary>xaiMaxTools upstream.</summary>
    public const int MaxTools = 200;

    private static readonly string[] DroppedFields =
        ["previous_response_id", "prompt_cache_retention", "safety_identifier", "stream_options", "stop"];

    public static void Apply(JsonObject body, XaiModelInfo? model)
    {
        ArgumentNullException.ThrowIfNull(body);

        foreach (var field in DroppedFields)
            body.Remove(field);

        ClampTools(body);
        ClampReasoningEffort(body, model);
        SanitizeInput(body);
    }

    private static void ClampTools(JsonObject body)
    {
        if (body["tools"] is not JsonArray tools)
            return;

        while (tools.Count > MaxTools)
            tools.RemoveAt(tools.Count - 1);
    }

    private static void ClampReasoningEffort(JsonObject body, XaiModelInfo? model)
    {
        if (model is null || body["reasoning"] is not JsonObject reasoning)
            return;

        var requested = reasoning["effort"]?.GetValue<string>();
        if (requested is null)
            return;

        var clamped = XaiGrokModelCatalog.ClampEffort(requested, model.ThinkingLevels);
        if (clamped is null)
            reasoning.Remove("effort");
        else
            reasoning["effort"] = clamped;

        if (reasoning.Count == 0)
            body.Remove("reasoning");
    }

    private static void SanitizeInput(JsonObject body)
    {
        if (body["input"] is not JsonArray input)
            return;

        var kept = new List<JsonNode?>(input.Count);
        JsonObject? previousBlankReasoning = null;

        foreach (var node in input)
        {
            if (node is not JsonObject item)
            {
                kept.Add(node?.DeepClone());
                previousBlankReasoning = null;
                continue;
            }

            var type = item["type"]?.GetValue<string>();
            if (type == "compaction")
            {
                if (XaiEncryptedContentValidator.Inspect(StringOrNull(item["encrypted_content"])).IsError)
                    continue; // foreign/invalid compaction blob: drop the item

                kept.Add(item.DeepClone());
                previousBlankReasoning = null;
                continue;
            }

            if (type != "reasoning")
            {
                kept.Add(item.DeepClone());
                previousBlankReasoning = null;
                continue;
            }

            var reasoning = (JsonObject)item.DeepClone();
            if (reasoning["content"] is null)
                reasoning.Remove("content");

            var encrypted = StringOrNull(reasoning["encrypted_content"]);
            if (encrypted is null || XaiEncryptedContentValidator.Inspect(encrypted).IsError)
                reasoning.Remove("encrypted_content");

            // Summary-only reasoning items carry no blob; adjacent ones collapse into one.
            if (reasoning["encrypted_content"] is null && reasoning["summary"] is JsonArray summary)
            {
                if (previousBlankReasoning?["summary"] is JsonArray previousSummary)
                {
                    foreach (var part in summary.ToList())
                    {
                        summary.Remove(part);
                        previousSummary.Add(part);
                    }

                    continue;
                }

                previousBlankReasoning = reasoning;
            }
            else
            {
                previousBlankReasoning = null;
            }

            kept.Add(reasoning);
        }

        body["input"] = new JsonArray(kept.ToArray());
    }

    private static string? StringOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

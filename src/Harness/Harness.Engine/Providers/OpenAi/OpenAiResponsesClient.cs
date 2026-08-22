using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>POST /responses (streaming SSE).</summary>
public sealed class OpenAiResponsesClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async IAsyncEnumerable<Result<OpenAiStreamChunk, string>> StreamCreateAsync(
        OpenAiCompatibleAgentProvider provider,
        OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest built,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(built);

        var baseUrl = OpenAiCompatibleHttp.NormalizeBaseUrl(provider.BaseUrl);
        var url = $"{baseUrl}/responses";
        var body = BuildCreateBody(provider, built);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var functionCalls = new Dictionary<string, ResponsesFunctionSlot>(StringComparer.Ordinal);
        string? responseId = null;
        JsonObject? completedResponse = null;
        string? streamError = null;

        await foreach (var payload in OpenAiCompatibleHttp
            .ReadSseJsonPayloadsAsync(_http, HttpMethod.Post, url, provider.ApiKey, body, cancellationToken)
            .ConfigureAwait(false))
        {
            if (payload.IsError)
            {
                yield return Result<OpenAiStreamChunk, string>.AsError(payload.Error);
                yield break;
            }

            JsonNode? node;
            string? parseError = null;
            try
            {
                node = JsonNode.Parse(payload.Value);
            }
            catch (JsonException ex)
            {
                parseError = $"Invalid JSON in Responses stream: {ex.Message}";
                node = null;
            }

            if (parseError is not null)
            {
                yield return Result<OpenAiStreamChunk, string>.AsError(parseError);
                yield break;
            }

            if (node is not JsonObject obj)
                continue;

            var eventType = obj["type"]?.GetValue<string>();
            responseId ??= obj["response"]?["id"]?.GetValue<string>()
                ?? obj["response_id"]?.GetValue<string>();

            if (string.Equals(eventType, "error", StringComparison.Ordinal)
                || string.Equals(eventType, "response.failed", StringComparison.Ordinal))
            {
                streamError = FormatResponsesStreamError(obj);
                break;
            }

            string? textDelta = null;
            string? reasoningDelta = null;
            List<OpenAiStreamToolCallDelta>? toolDeltas = null;

            if (string.Equals(eventType, "response.created", StringComparison.Ordinal)
                || string.Equals(eventType, "response.in_progress", StringComparison.Ordinal))
            {
                responseId ??= obj["response"]?["id"]?.GetValue<string>();
                continue;
            }

            if (string.Equals(eventType, "response.output_text.delta", StringComparison.Ordinal))
            {
                var delta = TryGetString(obj["delta"]);
                if (!string.IsNullOrEmpty(delta))
                {
                    content.Append(delta);
                    textDelta = delta;
                }
            }
            else if (string.Equals(eventType, "response.reasoning_summary_text.delta", StringComparison.Ordinal)
                || string.Equals(eventType, "response.reasoning_text.delta", StringComparison.Ordinal))
            {
                var delta = TryGetString(obj["delta"]);
                if (!string.IsNullOrEmpty(delta))
                {
                    reasoning.Append(delta);
                    reasoningDelta = delta;
                }
            }
            else if (string.Equals(eventType, "response.output_item.added", StringComparison.Ordinal))
            {
                if (obj["item"] is JsonObject item
                    && string.Equals(item["type"]?.GetValue<string>(), "function_call", StringComparison.Ordinal))
                {
                    var itemId = ResolveFunctionItemKey(item);
                    if (itemId is not null)
                    {
                        var slot = GetOrCreateFunctionSlot(functionCalls, itemId);
                        ApplyFunctionCallFields(slot, item, obj["output_index"]?.GetValue<int>());

                        if (OpenAiCompatibleHttp.IsUsableResponsesCallId(slot.CallId))
                        {
                            toolDeltas =
                            [
                                new OpenAiStreamToolCallDelta
                                {
                                    Index = slot.OutputIndex,
                                    CallId = slot.CallId,
                                    ToolName = slot.ToolName,
                                },
                            ];
                        }
                    }
                }
            }
            else if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.Ordinal))
            {
                var itemId = TryGetString(obj["item_id"]);
                if (!string.IsNullOrEmpty(itemId))
                {
                    var slot = GetOrCreateFunctionSlot(functionCalls, itemId);
                    if (obj["output_index"]?.GetValue<int>() is int idx)
                        slot.OutputIndex = Math.Min(slot.OutputIndex, idx);

                    var delta = TryGetString(obj["delta"]);
                    if (!string.IsNullOrEmpty(delta))
                        slot.Arguments.Append(delta);

                    toolDeltas =
                    [
                        new OpenAiStreamToolCallDelta
                        {
                            Index = slot.OutputIndex == int.MaxValue ? 0 : slot.OutputIndex,
                            CallId = OpenAiCompatibleHttp.IsUsableResponsesCallId(slot.CallId)
                                ? slot.CallId
                                : null,
                            ToolName = slot.ToolName,
                            ArgumentsDelta = delta,
                        },
                    ];
                }
            }
            else if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.Ordinal))
            {
                // Authoritative full arguments; name is often omitted by the live API — keep from added.
                var itemId = TryGetString(obj["item_id"]);
                if (!string.IsNullOrEmpty(itemId))
                {
                    var slot = GetOrCreateFunctionSlot(functionCalls, itemId);
                    var name = TryGetString(obj["name"]);
                    if (!string.IsNullOrEmpty(name))
                        slot.ToolName = name;

                    var args = TryGetString(obj["arguments"]);
                    if (args is not null)
                    {
                        slot.Arguments.Clear();
                        slot.Arguments.Append(args);
                    }
                }
            }
            else if (string.Equals(eventType, "response.output_item.done", StringComparison.Ordinal))
            {
                if (obj["item"] is JsonObject item
                    && string.Equals(item["type"]?.GetValue<string>(), "function_call", StringComparison.Ordinal))
                {
                    var itemId = ResolveFunctionItemKey(item);
                    if (itemId is not null)
                    {
                        var slot = GetOrCreateFunctionSlot(functionCalls, itemId);
                        ApplyFunctionCallFields(slot, item, obj["output_index"]?.GetValue<int>());
                    }
                }
            }
            else if (string.Equals(eventType, "response.completed", StringComparison.Ordinal))
            {
                completedResponse = obj["response"] as JsonObject;
                responseId ??= completedResponse?["id"]?.GetValue<string>();
            }

            if (textDelta is not null || reasoningDelta is not null || toolDeltas is { Count: > 0 })
            {
                yield return Result<OpenAiStreamChunk, string>.AsValue(new OpenAiStreamChunk
                {
                    TextDelta = textDelta,
                    ReasoningDelta = reasoningDelta,
                    ToolCallDeltas = toolDeltas,
                });
            }
        }

        if (streamError is not null)
        {
            yield return Result<OpenAiStreamChunk, string>.AsError(streamError);
            yield break;
        }

        var toolCalls = MergeToolCalls(functionCalls, completedResponse);
        var reasoningItems = ExtractRawReasoningItems(completedResponse);
        var usageHint = completedResponse is not null
            ? OpenAiCompatibleHttp.FormatUsageCacheHint(completedResponse)
            : null;
        var promptTokens = completedResponse is not null
            ? OpenAiCompatibleHttp.TryParsePromptTokens(completedResponse)
            : null;
        DysonParsedUsage? parsedUsage = null;
        if (completedResponse is not null
            && OpenAiCompatibleHttp.TryParseUsage(completedResponse, out var streamUsage))
        {
            parsedUsage = streamUsage;
        }

        // Prefer streamed accumulation; fall back to completed response payload if empty.
        var reasoningContent = reasoning.Length == 0
            ? ExtractReasoningFromResponse(completedResponse)
            : reasoning.ToString();

        yield return Result<OpenAiStreamChunk, string>.AsValue(new OpenAiStreamChunk
        {
            IsRoundComplete = true,
            CompletedReply = new OpenAiModelReply
            {
                Content = content.Length == 0 ? null : content.ToString(),
                ReasoningContent = reasoningContent,
                ToolCalls = toolCalls,
                ResponseId = responseId,
                UsageCacheHint = usageHint,
                PromptTokens = promptTokens,
                ReasoningOutputItems = reasoningItems,
                Usage = parsedUsage,
            },
        });
    }

    /// <summary>
    /// Builds the POST /responses JSON body (nested <c>reasoning.effort</c>, not top-level <c>reasoning_effort</c>).
    /// When <c>store: false</c>, requests <c>reasoning.encrypted_content</c> for stateless replay.
    /// </summary>
    public static JsonObject BuildCreateBody(
        OpenAiCompatibleAgentProvider provider,
        OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest built)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(built);

        var body = new JsonObject
        {
            ["model"] = provider.Slug,
            ["instructions"] = built.Instructions,
            ["input"] = built.Input.DeepClone(),
            ["tools"] = built.Tools.DeepClone(),
            ["prompt_cache_key"] = built.PromptCacheKey,
            ["store"] = built.Store,
            ["stream"] = true,
        };

        if (!string.IsNullOrWhiteSpace(built.PreviousResponseId))
            body["previous_response_id"] = built.PreviousResponseId;

        if (!built.Store)
        {
            body["include"] = new JsonArray
            {
                "reasoning.encrypted_content",
            };
        }

        if (built.IncludeExplicitBreakpoints)
        {
            body["prompt_cache_options"] = new JsonObject
            {
                ["mode"] = "explicit",
            };
        }

        if (!string.IsNullOrWhiteSpace(provider.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = provider.ReasoningEffort.Trim() };

        return body;
    }

    public static Result<OpenAiModelReply, string> Parse(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var output = response["output"] as JsonArray;
        if (output is null)
            return Result<OpenAiModelReply, string>.AsError("Responses payload had no output array.");

        var contentParts = new List<string>();
        var reasoningParts = new List<string>();
        var toolCalls = new List<DysonToolCall>();
        var reasoningItems = new List<JsonObject>();

        foreach (var item in output)
        {
            if (item is not JsonObject obj)
                continue;

            var type = obj["type"]?.GetValue<string>();
            if (string.Equals(type, "message", StringComparison.Ordinal))
            {
                if (obj["content"] is JsonArray parts)
                {
                    foreach (var part in parts)
                    {
                        if (part is not JsonObject p)
                            continue;
                        var partType = p["type"]?.GetValue<string>();
                        if (string.Equals(partType, "output_text", StringComparison.Ordinal)
                            || string.Equals(partType, "text", StringComparison.Ordinal))
                        {
                            var text = p["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(text))
                                contentParts.Add(text);
                        }
                    }
                }
            }
            else if (string.Equals(type, "reasoning", StringComparison.Ordinal))
            {
                AppendReasoningParts(obj, reasoningParts);
                reasoningItems.Add((JsonObject)obj.DeepClone());
            }
            else if (string.Equals(type, "function_call", StringComparison.Ordinal))
            {
                var callId = TryGetUsableCallId(obj);
                var name = obj["name"]?.GetValue<string>() ?? "";
                var args = obj["arguments"]?.GetValue<string>() ?? "{}";
                if (string.IsNullOrWhiteSpace(name) || callId is null)
                    continue;

                var (stage, argsClean) = OpenAiCompatibleHttp.SplitStageFromArguments(args);
                toolCalls.Add(new DysonToolCall
                {
                    CallId = callId,
                    ToolName = name,
                    Stage = stage,
                    ArgumentsJson = argsClean,
                });
            }
        }

        var content = contentParts.Count == 0 ? null : string.Join("\n", contentParts);
        var reasoningContent = reasoningParts.Count == 0 ? null : string.Join("\n", reasoningParts);
        return Result<OpenAiModelReply, string>.AsValue(new OpenAiModelReply
        {
            Content = content,
            ReasoningContent = reasoningContent,
            ToolCalls = toolCalls,
            ResponseId = response["id"]?.GetValue<string>(),
            UsageCacheHint = OpenAiCompatibleHttp.FormatUsageCacheHint(response),
            PromptTokens = OpenAiCompatibleHttp.TryParsePromptTokens(response),
            ReasoningOutputItems = reasoningItems,
            Usage = OpenAiCompatibleHttp.TryParseUsage(response, out var parsed) ? parsed : null,
        });
    }

    private static string? ExtractReasoningFromResponse(JsonObject? response)
    {
        if (response is null)
            return null;

        var parts = new List<string>();
        if (response["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                if (item is JsonObject obj
                    && string.Equals(obj["type"]?.GetValue<string>(), "reasoning", StringComparison.Ordinal))
                {
                    AppendReasoningParts(obj, parts);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static IReadOnlyList<JsonObject> ExtractRawReasoningItems(JsonObject? response)
    {
        if (response?["output"] is not JsonArray output)
            return [];

        var items = new List<JsonObject>();
        foreach (var item in output)
        {
            if (item is JsonObject obj
                && string.Equals(obj["type"]?.GetValue<string>(), "reasoning", StringComparison.Ordinal))
            {
                items.Add((JsonObject)obj.DeepClone());
            }
        }

        return items;
    }

    private static void AppendReasoningParts(JsonObject reasoningItem, List<string> parts)
    {
        // summary: [{ type: "summary_text", text: "..." }, ...]
        if (reasoningItem["summary"] is JsonArray summary)
        {
            foreach (var part in summary)
            {
                if (part is not JsonObject p)
                    continue;
                var text = TryGetString(p["text"]);
                if (!string.IsNullOrEmpty(text))
                    parts.Add(text);
            }
        }

        // content: [{ type: "reasoning_text", text: "..." }, ...]
        if (reasoningItem["content"] is JsonArray content)
        {
            foreach (var part in content)
            {
                if (part is not JsonObject p)
                    continue;
                var text = TryGetString(p["text"]);
                if (!string.IsNullOrEmpty(text))
                    parts.Add(text);
            }
        }
    }

    private static ResponsesFunctionSlot GetOrCreateFunctionSlot(
        Dictionary<string, ResponsesFunctionSlot> slots,
        string itemId)
    {
        if (!slots.TryGetValue(itemId, out var slot))
        {
            slot = new ResponsesFunctionSlot();
            slots[itemId] = slot;
        }

        return slot;
    }

    /// <summary>
    /// Slot key for SSE assembly: prefer item <c>id</c> (<c>fc_…</c>); never invent a Guid.
    /// </summary>
    private static string? ResolveFunctionItemKey(JsonObject item)
    {
        var id = TryGetString(item["id"]);
        if (!string.IsNullOrEmpty(id))
            return id;

        var callId = TryGetString(item["call_id"]);
        return string.IsNullOrEmpty(callId) ? null : callId;
    }

    private static void ApplyFunctionCallFields(
        ResponsesFunctionSlot slot,
        JsonObject item,
        int? outputIndex)
    {
        var callId = TryGetUsableCallId(item);
        if (callId is not null)
            slot.CallId = callId;

        var name = TryGetString(item["name"]);
        if (!string.IsNullOrEmpty(name))
            slot.ToolName = name;

        if (TryGetString(item["arguments"]) is { Length: > 0 } args)
        {
            slot.Arguments.Clear();
            slot.Arguments.Append(args);
        }

        if (outputIndex is int idx)
            slot.OutputIndex = Math.Min(slot.OutputIndex, idx);
    }

    private static string? TryGetUsableCallId(JsonObject item)
    {
        var callId = TryGetString(item["call_id"]);
        return OpenAiCompatibleHttp.IsUsableResponsesCallId(callId) ? callId : null;
    }

    /// <summary>
    /// Prefer <c>response.completed.output</c> (ordered) when present; else stream slots by output_index.
    /// Never emits <c>fc_*</c> / Guid as <c>call_id</c>.
    /// </summary>
    private static List<DysonToolCall> MergeToolCalls(
        Dictionary<string, ResponsesFunctionSlot> slots,
        JsonObject? completedResponse)
    {
        if (completedResponse?["output"] is JsonArray output)
        {
            var fromCompleted = new List<DysonToolCall>();
            foreach (var item in output)
            {
                if (item is not JsonObject obj)
                    continue;
                if (!string.Equals(obj["type"]?.GetValue<string>(), "function_call", StringComparison.Ordinal))
                    continue;

                var callId = TryGetUsableCallId(obj);
                var name = TryGetString(obj["name"]);
                if (callId is null || string.IsNullOrWhiteSpace(name))
                    continue;

                var args = TryGetString(obj["arguments"]) ?? "{}";
                var (stage, argsClean) = OpenAiCompatibleHttp.SplitStageFromArguments(args);
                fromCompleted.Add(new DysonToolCall
                {
                    CallId = callId,
                    ToolName = name,
                    Stage = stage,
                    ArgumentsJson = argsClean,
                });
            }

            if (fromCompleted.Count > 0)
                return fromCompleted;
        }

        return BuildToolCallsFromSlots(slots);
    }

    private static List<DysonToolCall> BuildToolCallsFromSlots(Dictionary<string, ResponsesFunctionSlot> slots)
    {
        var ordered = slots.Values
            .OrderBy(s => s.OutputIndex)
            .ThenBy(s => s.CallId, StringComparer.Ordinal);

        var toolCalls = new List<DysonToolCall>();
        foreach (var slot in ordered)
        {
            if (string.IsNullOrWhiteSpace(slot.ToolName))
                continue;
            if (!OpenAiCompatibleHttp.IsUsableResponsesCallId(slot.CallId))
                continue;

            var args = slot.Arguments.Length == 0 ? "{}" : slot.Arguments.ToString();
            var (stage, argsClean) = OpenAiCompatibleHttp.SplitStageFromArguments(args);
            toolCalls.Add(new DysonToolCall
            {
                CallId = slot.CallId!,
                ToolName = slot.ToolName,
                Stage = stage,
                ArgumentsJson = argsClean,
            });
        }

        return toolCalls;
    }

    private static string FormatResponsesStreamError(JsonObject obj)
    {
        var err = obj["error"] as JsonObject;
        var message = TryGetString(err?["message"])
            ?? TryGetString(obj["message"])
            ?? obj.ToJsonString(OpenAiCompatibleHttp.JsonOptions);
        var code = TryGetString(err?["code"]) ?? TryGetString(obj["code"]);
        return string.IsNullOrEmpty(code)
            ? $"OpenAI Responses stream error: {message}"
            : $"OpenAI Responses stream error ({code}): {message}";
    }

    private static string? TryGetString(JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class ResponsesFunctionSlot
    {
        public string? CallId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder Arguments { get; } = new();
        public int OutputIndex { get; set; } = int.MaxValue;
    }
}

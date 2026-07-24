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

        if (built.IncludeExplicitBreakpoints)
        {
            body["prompt_cache_options"] = new JsonObject
            {
                ["mode"] = "explicit",
            };
        }

        var content = new StringBuilder();
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
            else if (string.Equals(eventType, "response.output_item.added", StringComparison.Ordinal))
            {
                if (obj["item"] is JsonObject item
                    && string.Equals(item["type"]?.GetValue<string>(), "function_call", StringComparison.Ordinal))
                {
                    var itemId = TryGetString(item["id"])
                        ?? TryGetString(item["call_id"])
                        ?? Guid.NewGuid().ToString("N");
                    var slot = GetOrCreateFunctionSlot(functionCalls, itemId);
                    slot.CallId = TryGetString(item["call_id"]) ?? itemId;
                    slot.ToolName = TryGetString(item["name"]);
                    slot.Arguments.Clear();
                    if (TryGetString(item["arguments"]) is { Length: > 0 } initialArgs)
                        slot.Arguments.Append(initialArgs);

                    toolDeltas =
                    [
                        new OpenAiStreamToolCallDelta
                        {
                            Index = obj["output_index"]?.GetValue<int>() ?? 0,
                            CallId = slot.CallId,
                            ToolName = slot.ToolName,
                        },
                    ];
                }
            }
            else if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.Ordinal))
            {
                var itemId = TryGetString(obj["item_id"]) ?? "";
                if (!string.IsNullOrEmpty(itemId))
                {
                    var slot = GetOrCreateFunctionSlot(functionCalls, itemId);
                    var delta = TryGetString(obj["delta"]);
                    if (!string.IsNullOrEmpty(delta))
                        slot.Arguments.Append(delta);

                    toolDeltas =
                    [
                        new OpenAiStreamToolCallDelta
                        {
                            Index = obj["output_index"]?.GetValue<int>() ?? 0,
                            CallId = slot.CallId ?? itemId,
                            ToolName = slot.ToolName,
                            ArgumentsDelta = delta,
                        },
                    ];
                }
            }
            else if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.Ordinal))
            {
                // Authoritative full arguments; name is often omitted by the live API — keep from added.
                var itemId = TryGetString(obj["item_id"]) ?? "";
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
                    var itemId = TryGetString(item["id"])
                        ?? TryGetString(item["call_id"])
                        ?? "";
                    if (!string.IsNullOrEmpty(itemId))
                    {
                        var slot = GetOrCreateFunctionSlot(functionCalls, itemId);
                        slot.CallId = TryGetString(item["call_id"]) ?? itemId;
                        slot.ToolName = TryGetString(item["name"]) ?? slot.ToolName;
                        if (TryGetString(item["arguments"]) is { } args)
                        {
                            slot.Arguments.Clear();
                            slot.Arguments.Append(args);
                        }
                    }
                }
            }
            else if (string.Equals(eventType, "response.completed", StringComparison.Ordinal))
            {
                completedResponse = obj["response"] as JsonObject;
                responseId ??= completedResponse?["id"]?.GetValue<string>();
            }

            if (textDelta is not null || toolDeltas is { Count: > 0 })
            {
                yield return Result<OpenAiStreamChunk, string>.AsValue(new OpenAiStreamChunk
                {
                    TextDelta = textDelta,
                    ToolCallDeltas = toolDeltas,
                });
            }
        }

        if (streamError is not null)
        {
            yield return Result<OpenAiStreamChunk, string>.AsError(streamError);
            yield break;
        }

        var toolCalls = BuildToolCalls(functionCalls);
        var usageHint = completedResponse is not null
            ? OpenAiCompatibleHttp.FormatUsageCacheHint(completedResponse)
            : null;

        yield return Result<OpenAiStreamChunk, string>.AsValue(new OpenAiStreamChunk
        {
            IsRoundComplete = true,
            CompletedReply = new OpenAiModelReply
            {
                Content = content.Length == 0 ? null : content.ToString(),
                ToolCalls = toolCalls,
                ResponseId = responseId,
                UsageCacheHint = usageHint,
            },
        });
    }

    public static Result<OpenAiModelReply, string> Parse(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var output = response["output"] as JsonArray;
        if (output is null)
            return Result<OpenAiModelReply, string>.AsError("Responses payload had no output array.");

        var contentParts = new List<string>();
        var toolCalls = new List<DysonToolCall>();

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
            else if (string.Equals(type, "function_call", StringComparison.Ordinal))
            {
                var id = obj["call_id"]?.GetValue<string>()
                    ?? obj["id"]?.GetValue<string>()
                    ?? Guid.NewGuid().ToString("N");
                var name = obj["name"]?.GetValue<string>() ?? "";
                var args = obj["arguments"]?.GetValue<string>() ?? "{}";
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var (stage, argsClean) = OpenAiCompatibleHttp.SplitStageFromArguments(args);
                toolCalls.Add(new DysonToolCall
                {
                    CallId = id,
                    ToolName = name,
                    Stage = stage,
                    ArgumentsJson = argsClean,
                });
            }
        }

        var content = contentParts.Count == 0 ? null : string.Join("\n", contentParts);
        return Result<OpenAiModelReply, string>.AsValue(new OpenAiModelReply
        {
            Content = content,
            ToolCalls = toolCalls,
            ResponseId = response["id"]?.GetValue<string>(),
            UsageCacheHint = OpenAiCompatibleHttp.FormatUsageCacheHint(response),
        });
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

    private static List<DysonToolCall> BuildToolCalls(Dictionary<string, ResponsesFunctionSlot> slots)
    {
        var toolCalls = new List<DysonToolCall>();
        foreach (var slot in slots.Values)
        {
            if (string.IsNullOrWhiteSpace(slot.ToolName))
                continue;

            var args = slot.Arguments.Length == 0 ? "{}" : slot.Arguments.ToString();
            var (stage, argsClean) = OpenAiCompatibleHttp.SplitStageFromArguments(args);
            toolCalls.Add(new DysonToolCall
            {
                CallId = string.IsNullOrEmpty(slot.CallId) ? Guid.NewGuid().ToString("N") : slot.CallId,
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
    }
}

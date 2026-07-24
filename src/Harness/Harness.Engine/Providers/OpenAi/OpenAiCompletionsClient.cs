using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>POST /chat/completions (streaming SSE).</summary>
public sealed class OpenAiCompletionsClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async IAsyncEnumerable<Result<OpenAiStreamChunk, string>> StreamCreateAsync(
        OpenAiCompatibleAgentProvider provider,
        OpenAiCacheFriendlyTranscriptBuilder.BuiltCompletionsRequest built,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(built);

        var baseUrl = OpenAiCompatibleHttp.NormalizeBaseUrl(provider.BaseUrl);
        var url = $"{baseUrl}/chat/completions";

        var body = new JsonObject
        {
            ["model"] = provider.Slug,
            ["messages"] = built.Messages.DeepClone(),
            ["tools"] = built.Tools.DeepClone(),
            ["prompt_cache_key"] = built.PromptCacheKey,
            ["stream"] = true,
            ["stream_options"] = new JsonObject
            {
                ["include_usage"] = true,
            },
        };

        if (built.IncludeExplicitBreakpoints)
        {
            body["prompt_cache_options"] = new JsonObject
            {
                ["mode"] = "explicit",
            };
        }

        var content = new StringBuilder();
        var toolSlots = new Dictionary<int, CompletionsToolSlot>();
        string? responseId = null;
        JsonObject? usageResponse = null;

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
                parseError = $"Invalid JSON in Completions stream: {ex.Message}";
                node = null;
            }

            if (parseError is not null)
            {
                yield return Result<OpenAiStreamChunk, string>.AsError(parseError);
                yield break;
            }

            if (node is not JsonObject obj)
                continue;

            responseId ??= obj["id"]?.GetValue<string>();

            if (obj["usage"] is JsonObject usage)
                usageResponse = obj;

            var choices = obj["choices"] as JsonArray;
            if (choices is null || choices.Count == 0)
                continue;

            var choice = choices[0] as JsonObject;
            if (choice is null)
                continue;

            // Final usage-only chunk (stream_options.include_usage) has empty choices or no delta.
            if (choice["delta"] is not JsonObject delta)
                continue;

            string? textDelta = null;
            List<OpenAiStreamToolCallDelta>? toolDeltas = null;

            var deltaContent = TryGetString(delta["content"]);
            if (!string.IsNullOrEmpty(deltaContent))
            {
                content.Append(deltaContent);
                textDelta = deltaContent;
            }

            if (delta["tool_calls"] is JsonArray toolArr)
            {
                toolDeltas = [];
                foreach (var item in toolArr)
                {
                    if (item is not JsonObject tc)
                        continue;

                    var index = tc["index"]?.GetValue<int>() ?? 0;
                    if (!toolSlots.TryGetValue(index, out var slot))
                    {
                        slot = new CompletionsToolSlot();
                        toolSlots[index] = slot;
                    }

                    var id = TryGetString(tc["id"]);
                    if (!string.IsNullOrEmpty(id))
                        slot.CallId = id;

                    if (tc["function"] is JsonObject fn)
                    {
                        var name = TryGetString(fn["name"]);
                        if (!string.IsNullOrEmpty(name))
                            slot.ToolName = name;

                        var argsPart = TryGetString(fn["arguments"]);
                        if (!string.IsNullOrEmpty(argsPart))
                            slot.Arguments.Append(argsPart);
                    }

                    toolDeltas.Add(new OpenAiStreamToolCallDelta
                    {
                        Index = index,
                        CallId = id,
                        ToolName = TryGetString(tc["function"]?["name"]),
                        ArgumentsDelta = TryGetString(tc["function"]?["arguments"]),
                    });
                }
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

        var toolCalls = BuildToolCalls(toolSlots);
        var usageHint = usageResponse is not null
            ? OpenAiCompatibleHttp.FormatUsageCacheHint(usageResponse)
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

        var choices = response["choices"] as JsonArray;
        if (choices is null || choices.Count == 0)
            return Result<OpenAiModelReply, string>.AsError("Completions response had no choices.");

        var message = choices[0]?["message"] as JsonObject;
        if (message is null)
            return Result<OpenAiModelReply, string>.AsError("Completions choice had no message.");

        var content = message["content"]?.GetValue<string>();
        var toolCalls = new List<DysonToolCall>();
        if (message["tool_calls"] is JsonArray toolArr)
        {
            foreach (var item in toolArr)
            {
                if (item is not JsonObject tc)
                    continue;

                var id = tc["id"]?.GetValue<string>() ?? "";
                var fn = tc["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var args = fn?["arguments"]?.GetValue<string>() ?? "{}";
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var (stage, argsClean) = OpenAiCompatibleHttp.SplitStageFromArguments(args);
                toolCalls.Add(new DysonToolCall
                {
                    CallId = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id,
                    ToolName = name,
                    Stage = stage,
                    ArgumentsJson = argsClean,
                });
            }
        }

        return Result<OpenAiModelReply, string>.AsValue(new OpenAiModelReply
        {
            Content = content,
            ToolCalls = toolCalls,
            ResponseId = response["id"]?.GetValue<string>(),
            UsageCacheHint = OpenAiCompatibleHttp.FormatUsageCacheHint(response),
        });
    }

    private static List<DysonToolCall> BuildToolCalls(Dictionary<int, CompletionsToolSlot> slots)
    {
        var toolCalls = new List<DysonToolCall>();
        foreach (var (_, slot) in slots.OrderBy(kv => kv.Key))
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

    private sealed class CompletionsToolSlot
    {
        public string? CallId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}

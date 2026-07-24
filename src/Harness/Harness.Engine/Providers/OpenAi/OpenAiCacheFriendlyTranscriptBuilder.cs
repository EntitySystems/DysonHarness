using System.Text;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Builds cache-friendly Completions messages / Responses input from local turn history.
/// Stable prefix first (system + tools catalog text), dynamic content last; never mutates
/// already-optimized turn sections.
/// </summary>
public static class OpenAiCacheFriendlyTranscriptBuilder
{
    public sealed record BuiltCompletionsRequest(
        JsonArray Messages,
        JsonArray Tools,
        string PromptCacheKey,
        bool IncludeExplicitBreakpoints);

    public sealed record BuiltResponsesRequest(
        string Instructions,
        JsonArray Input,
        JsonArray Tools,
        string PromptCacheKey,
        bool IncludeExplicitBreakpoints,
        string? PreviousResponseId,
        bool Store);

    public sealed record InFlightToolRound(
        IReadOnlyList<DysonToolCall> Calls,
        IReadOnlyList<DysonToolCallResult> Results);

    /// <summary>System text: mode prompt + MCP catalog (stable prefix for Completions).</summary>
    public static string BuildSystemText(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sb = new StringBuilder();
        sb.AppendLine(session.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine(session.McpPipeline.FormatToolsForPrompt());
        sb.AppendLine();
        sb.AppendLine(
            "Reply format: start every final assistant message with a Markdown H1 title " +
            "(\"# …\"), then the body. Include harness field `stage` (integer) on every tool call.");
        return sb.ToString().TrimEnd();
    }

    public static BuiltCompletionsRequest BuildCompletions(
        DysonAgentSession session,
        string? currentUserPrompt,
        IReadOnlyList<string>? currentFilePaths,
        IReadOnlyList<InFlightToolRound>? inFlightRounds = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var includeBreakpoints = session.Provider is OpenAiCompatibleAgentProvider oai
            && OpenAiCompatibleHttp.LooksLikeGpt56OrNewer(oai.Slug);

        var systemText = BuildSystemText(session);
        var messages = new JsonArray();

        var systemContent = BuildTextContentParts(systemText, includeBreakpoints);
        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = systemContent,
        });

        AppendHistoryMessages(messages, session.Turns, excludeLastIfCurrent: false);

        if (!string.IsNullOrEmpty(currentUserPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = FormatUserContent(currentUserPrompt, currentFilePaths),
            });
        }

        if (inFlightRounds is not null)
        {
            foreach (var round in inFlightRounds)
                AppendToolRoundCompletions(messages, round);
        }

        return new BuiltCompletionsRequest(
            messages,
            OpenAiCompatibleHttp.BuildToolsArray(session.McpPipeline),
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId),
            includeBreakpoints);
    }

    /// <summary>
    /// Full Responses rebuild (after compaction or new user turn). Prefer <c>store: false</c>.
    /// </summary>
    public static BuiltResponsesRequest BuildResponsesFull(
        DysonAgentSession session,
        string? currentUserPrompt,
        IReadOnlyList<string>? currentFilePaths,
        IReadOnlyList<InFlightToolRound>? inFlightRounds = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var includeBreakpoints = session.Provider is OpenAiCompatibleAgentProvider oai
            && OpenAiCompatibleHttp.LooksLikeGpt56OrNewer(oai.Slug);

        var instructions = BuildSystemText(session);
        var input = new JsonArray();

        AppendHistoryAsResponsesInput(input, session.Turns);

        if (!string.IsNullOrEmpty(currentUserPrompt))
        {
            input.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = FormatUserContent(currentUserPrompt, currentFilePaths),
            });
        }

        if (inFlightRounds is not null)
        {
            foreach (var round in inFlightRounds)
                AppendToolRoundResponses(input, round);
        }

        return new BuiltResponsesRequest(
            instructions,
            input,
            OpenAiCompatibleHttp.BuildResponsesToolsArray(session.McpPipeline),
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId),
            includeBreakpoints,
            PreviousResponseId: null,
            Store: false);
    }

    /// <summary>
    /// Responses delta within a tool loop: <c>previous_response_id</c> + only new function_call_output items.
    /// </summary>
    public static BuiltResponsesRequest BuildResponsesDelta(
        DysonAgentSession session,
        string previousResponseId,
        IReadOnlyList<DysonToolCallResult> newResults)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousResponseId);
        ArgumentNullException.ThrowIfNull(newResults);

        var includeBreakpoints = session.Provider is OpenAiCompatibleAgentProvider oai
            && OpenAiCompatibleHttp.LooksLikeGpt56OrNewer(oai.Slug);

        var input = new JsonArray();
        foreach (var result in newResults)
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = result.CallId,
                ["output"] = result.IsError
                    ? $"[error] {result.Content}"
                    : result.Content,
            });
        }

        return new BuiltResponsesRequest(
            BuildSystemText(session),
            input,
            OpenAiCompatibleHttp.BuildResponsesToolsArray(session.McpPipeline),
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId),
            includeBreakpoints,
            PreviousResponseId: previousResponseId,
            Store: true);
    }

    private static JsonNode BuildTextContentParts(string text, bool includeBreakpoint)
    {
        if (!includeBreakpoint)
            return text;

        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["prompt_cache_breakpoint"] = true,
            },
        };
    }

    private static string FormatUserContent(string prompt, IReadOnlyList<string>? filePaths)
    {
        if (filePaths is null || filePaths.Count == 0)
            return prompt;

        var sb = new StringBuilder(prompt);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Attached paths:");
        foreach (var path in filePaths)
            sb.AppendLine($"- {path}");
        return sb.ToString().TrimEnd();
    }

    private static void AppendHistoryMessages(
        JsonArray messages,
        IReadOnlyList<DysonAgentTurn> turns,
        bool excludeLastIfCurrent)
    {
        var count = turns.Count;
        for (var i = 0; i < count; i++)
        {
            if (excludeLastIfCurrent && i == count - 1)
                break;

            var turn = turns[i];
            // In-progress current turn: user content may get ephemeral rename review;
            // tool rounds come from inFlightRounds.
            var incompleteCurrent = i == count - 1 && string.IsNullOrEmpty(turn.AssistantText);
            if (!string.IsNullOrEmpty(turn.Instruction))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatTurnUserContent(turn, i, incompleteCurrent),
                });
            }

            if (incompleteCurrent)
                continue;

            if (turn.ToolHistoryOptimized && !string.IsNullOrEmpty(turn.CompactToolHistory))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = turn.CompactToolHistory,
                });
                continue;
            }

            if (turn.ToolCalls.Count > 0)
            {
                var toolCalls = new JsonArray();
                foreach (var call in turn.ToolCalls)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.ToolName,
                            ["arguments"] = MergeStageIntoArgs(call),
                        },
                    });
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = (string?)null,
                    ["tool_calls"] = toolCalls,
                });

                foreach (var result in turn.ResponseLog)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId,
                        ["content"] = result.IsError
                            ? $"[error] {result.Content}"
                            : result.Content,
                    });
                }
            }

            if (!string.IsNullOrEmpty(turn.AssistantText))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = FormatAssistantReply(turn),
                });
            }
        }
    }

    private static void AppendHistoryAsResponsesInput(
        JsonArray input,
        IReadOnlyList<DysonAgentTurn> turns)
    {
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            var incompleteCurrent = i == turns.Count - 1 && string.IsNullOrEmpty(turn.AssistantText);
            if (!string.IsNullOrEmpty(turn.Instruction))
            {
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatTurnUserContent(turn, i, incompleteCurrent),
                });
            }

            if (incompleteCurrent)
                continue;

            if (turn.ToolHistoryOptimized && !string.IsNullOrEmpty(turn.CompactToolHistory))
            {
                input.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = turn.CompactToolHistory,
                });
                continue;
            }

            if (turn.ToolCalls.Count > 0)
            {
                foreach (var call in turn.ToolCalls)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.CallId,
                        ["name"] = call.ToolName,
                        ["arguments"] = MergeStageIntoArgs(call),
                    });
                }

                foreach (var result in turn.ResponseLog)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = result.CallId,
                        ["output"] = result.IsError
                            ? $"[error] {result.Content}"
                            : result.Content,
                    });
                }
            }

            if (!string.IsNullOrEmpty(turn.AssistantText))
            {
                input.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = FormatAssistantReply(turn),
                });
            }
        }
    }

    private static void AppendToolRoundCompletions(JsonArray messages, InFlightToolRound round)
    {
        var toolCalls = new JsonArray();
        foreach (var call in round.Calls)
        {
            toolCalls.Add(new JsonObject
            {
                ["id"] = call.CallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.ToolName,
                    ["arguments"] = MergeStageIntoArgs(call),
                },
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = (string?)null,
            ["tool_calls"] = toolCalls,
        });

        foreach (var result in round.Results)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = result.CallId,
                ["content"] = result.IsError
                    ? $"[error] {result.Content}"
                    : result.Content,
            });
        }
    }

    private static void AppendToolRoundResponses(JsonArray input, InFlightToolRound round)
    {
        foreach (var call in round.Calls)
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = call.CallId,
                ["name"] = call.ToolName,
                ["arguments"] = MergeStageIntoArgs(call),
            });
        }

        foreach (var result in round.Results)
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = result.CallId,
                ["output"] = result.IsError
                    ? $"[error] {result.Content}"
                    : result.Content,
            });
        }
    }

    private static string MergeStageIntoArgs(DysonToolCall call)
    {
        try
        {
            var node = JsonNode.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (node is not JsonObject obj)
                return call.ArgumentsJson;

            obj["stage"] = call.Stage;
            return obj.ToJsonString(OpenAiCompatibleHttp.JsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return call.ArgumentsJson;
        }
    }

    /// <summary>
    /// History turns always send clean <see cref="DysonAgentTurn.Instruction"/>.
    /// Incomplete current turn appends <see cref="DysonSessionInitialization.RenameSessionReviewMandate"/>
    /// only on rename-review slots (1-based indices 1, 9, 17, …).
    /// </summary>
    private static string FormatTurnUserContent(
        DysonAgentTurn turn,
        int zeroBasedIndex,
        bool incompleteCurrent)
    {
        var instruction = turn.Instruction!;
        if (!incompleteCurrent)
            return instruction;

        var oneBased = zeroBasedIndex + 1;
        if (DysonSessionInitialization.IsRenameReviewTurn(oneBased))
            return $"{instruction}\n\n{DysonSessionInitialization.RenameSessionReviewMandate}";

        return instruction;
    }

    private static string FormatAssistantReply(DysonAgentTurn turn)
    {
        if (string.IsNullOrEmpty(turn.AgentTitle))
            return turn.AssistantText ?? "";

        return $"# {turn.AgentTitle}\n\n{turn.AssistantText}";
    }
}

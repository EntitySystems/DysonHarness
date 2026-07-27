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
    /// <summary>
    /// Emitted when a tool_call / function_call has no matching ResponseLog / round result
    /// (cancelled turn, abandoned WaitForSubagent, etc.). Keeps Completions/Responses transcripts paired.
    /// </summary>
    public const string IncompleteToolResultContent =
        "Tool call did not complete (cancelled or interrupted).";

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
            && OpenAiCompatibleHttp.SupportsExplicitPromptCache(oai);

        var systemText = BuildSystemText(session);
        var messages = new JsonArray();

        var systemContent = BuildTextContentParts(systemText, includeBreakpoints);
        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = systemContent,
        });

        AppendHistoryMessages(messages, session, excludeLastIfCurrent: false);

        if (inFlightRounds is not null)
        {
            for (var i = 0; i < inFlightRounds.Count; i++)
            {
                // One-shot vision: only the unanswered (last) round keeps BinaryAttachment parts.
                AppendToolRoundCompletions(
                    messages,
                    inFlightRounds[i],
                    includeBinaryAttachments: i == inFlightRounds.Count - 1);
            }
        }

        // After in-flight rounds so harness follow-ups (e.g. SubmitSubagentReport nudge) land last.
        if (!string.IsNullOrEmpty(currentUserPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = FormatUserContent(currentUserPrompt, currentFilePaths),
            });
        }

        return new BuiltCompletionsRequest(
            messages,
            OpenAiCompatibleHttp.BuildToolsArray(session.McpPipeline),
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
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
            && OpenAiCompatibleHttp.SupportsExplicitPromptCache(oai);

        var instructions = BuildSystemText(session);
        var input = new JsonArray();

        AppendHistoryAsResponsesInput(input, session);

        if (inFlightRounds is not null)
        {
            for (var i = 0; i < inFlightRounds.Count; i++)
            {
                AppendToolRoundResponses(
                    input,
                    inFlightRounds[i],
                    includeBinaryAttachments: i == inFlightRounds.Count - 1);
            }
        }

        // After in-flight rounds so harness follow-ups (e.g. SubmitSubagentReport nudge) land last.
        if (!string.IsNullOrEmpty(currentUserPrompt))
        {
            input.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = FormatUserContent(currentUserPrompt, currentFilePaths),
            });
        }

        return new BuiltResponsesRequest(
            instructions,
            input,
            OpenAiCompatibleHttp.BuildResponsesToolsArray(session.McpPipeline),
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
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
            && OpenAiCompatibleHttp.SupportsExplicitPromptCache(oai);

        // ponytail: two-pass so BinaryAttachment never splits consecutive function_call_output.
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

        foreach (var result in newResults)
        {
            if (!result.IsError && result.BinaryAttachment is { } attachment)
                AppendResponsesBinaryAttachment(input, attachment);
        }

        return new BuiltResponsesRequest(
            BuildSystemText(session),
            input,
            OpenAiCompatibleHttp.BuildResponsesToolsArray(session.McpPipeline),
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
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
        DysonAgentSession session,
        bool excludeLastIfCurrent)
    {
        var turns = session.Turns;
        var incompleteIndex = FindIncompleteCurrentIndex(turns);
        var count = turns.Count;
        for (var i = 0; i < count; i++)
        {
            if (excludeLastIfCurrent && i == incompleteIndex)
                break;

            var turn = turns[i];
            if (turn.IsExcludedFromContext)
                continue;

            // In-progress current turn: user content may get ephemeral rename / Plan mandates;
            // tool rounds come from inFlightRounds. PlanResult may append after the live turn.
            var incompleteCurrent = i == incompleteIndex;
            if (!string.IsNullOrEmpty(turn.Instruction))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatTurnUserContent(session, turn, i, incompleteCurrent),
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

                // History turns already have assistant output — ack only, no multimodal re-emit.
                AppendPairedToolResultsCompletions(
                    messages,
                    turn.ToolCalls,
                    turn.ResponseLog,
                    includeBinaryAttachments: false);
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
        DysonAgentSession session)
    {
        var turns = session.Turns;
        var incompleteIndex = FindIncompleteCurrentIndex(turns);
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (turn.IsExcludedFromContext)
                continue;

            var incompleteCurrent = i == incompleteIndex;
            if (!string.IsNullOrEmpty(turn.Instruction))
            {
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatTurnUserContent(session, turn, i, incompleteCurrent),
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

                AppendPairedToolResultsResponses(
                    input,
                    turn.ToolCalls,
                    turn.ResponseLog,
                    includeBinaryAttachments: false);
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

    private static void AppendToolRoundCompletions(
        JsonArray messages,
        InFlightToolRound round,
        bool includeBinaryAttachments)
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

        AppendPairedToolResultsCompletions(
            messages,
            round.Calls,
            round.Results,
            includeBinaryAttachments);
    }

    private static void AppendToolRoundResponses(
        JsonArray input,
        InFlightToolRound round,
        bool includeBinaryAttachments)
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

        AppendPairedToolResultsResponses(
            input,
            round.Calls,
            round.Results,
            includeBinaryAttachments);
    }

    private static void AppendPairedToolResultsCompletions(
        JsonArray messages,
        IReadOnlyList<DysonToolCall> calls,
        IEnumerable<DysonToolCallResult> results,
        bool includeBinaryAttachments)
    {
        // ponytail: OpenAI requires consecutive role:tool after tool_calls; defer BinaryAttachment.
        var byCallId = IndexResultsByCallId(results);
        foreach (var call in calls)
        {
            byCallId.TryGetValue(call.CallId, out var result);
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.CallId,
                ["content"] = FormatToolResultContent(result),
            });
        }

        if (!includeBinaryAttachments)
            return;

        foreach (var call in calls)
        {
            if (byCallId.TryGetValue(call.CallId, out var result)
                && result is { IsError: false, BinaryAttachment: { } attachment })
            {
                AppendCompletionsBinaryAttachment(messages, attachment);
            }
        }
    }

    private static void AppendPairedToolResultsResponses(
        JsonArray input,
        IReadOnlyList<DysonToolCall> calls,
        IEnumerable<DysonToolCallResult> results,
        bool includeBinaryAttachments)
    {
        // ponytail: keep function_call_output consecutive; BinaryAttachment after the round.
        var byCallId = IndexResultsByCallId(results);
        foreach (var call in calls)
        {
            byCallId.TryGetValue(call.CallId, out var result);
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = call.CallId,
                ["output"] = FormatToolResultContent(result),
            });
        }

        if (!includeBinaryAttachments)
            return;

        foreach (var call in calls)
        {
            if (byCallId.TryGetValue(call.CallId, out var result)
                && result is { IsError: false, BinaryAttachment: { } attachment })
            {
                AppendResponsesBinaryAttachment(input, attachment);
            }
        }
    }

    /// <summary>
    /// Completions: short tool ack already emitted; follow with a user multimodal message
    /// carrying filename+extension on the file/image part.
    /// </summary>
    private static void AppendCompletionsBinaryAttachment(
        JsonArray messages,
        DysonBinaryAttachment attachment)
    {
        var dataUrl = BuildDataUrl(attachment);
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Loaded binary attachment: {attachment.FileName}",
            },
        };

        if (attachment.IsImage)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = dataUrl,
                },
                // Some OpenAI-compatible hosts accept filename alongside image_url.
                ["filename"] = attachment.FileName,
            });
        }
        else
        {
            parts.Add(new JsonObject
            {
                ["type"] = "file",
                ["file"] = new JsonObject
                {
                    ["filename"] = attachment.FileName,
                    ["file_data"] = dataUrl,
                },
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = parts,
        });
    }

    /// <summary>
    /// Responses: follow function_call_output with input_image / input_file (filename = name+ext).
    /// </summary>
    private static void AppendResponsesBinaryAttachment(
        JsonArray input,
        DysonBinaryAttachment attachment)
    {
        var dataUrl = BuildDataUrl(attachment);
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = $"Loaded binary attachment: {attachment.FileName}",
            },
        };

        if (attachment.IsImage)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = dataUrl,
                ["filename"] = attachment.FileName,
            });
        }
        else
        {
            parts.Add(new JsonObject
            {
                ["type"] = "input_file",
                ["filename"] = attachment.FileName,
                ["file_data"] = dataUrl,
            });
        }

        input.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = parts,
        });
    }

    private static string BuildDataUrl(DysonBinaryAttachment attachment) =>
        $"data:{attachment.MimeType};base64,{attachment.Base64Data}";

    private static Dictionary<string, DysonToolCallResult> IndexResultsByCallId(
        IEnumerable<DysonToolCallResult> results)
    {
        var map = new Dictionary<string, DysonToolCallResult>();
        foreach (var result in results)
            map.TryAdd(result.CallId, result);
        return map;
    }

    private static string FormatToolResultContent(DysonToolCallResult? result)
    {
        if (result is null)
            return IncompleteToolResultContent;

        return result.IsError
            ? $"[error] {result.Content}"
            : result.Content;
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
    /// Index of the in-flight prompt turn (may not be last when a PlanResult was appended mid-turn).
    /// </summary>
    private static int FindIncompleteCurrentIndex(IReadOnlyList<DysonAgentTurn> turns)
    {
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Kind == DysonAgentTurnKind.PlanResult)
                continue;
            if (string.IsNullOrEmpty(turns[i].AssistantText))
                return i;
            break;
        }

        return -1;
    }

    /// <summary>
    /// History turns always send clean <see cref="DysonAgentTurn.Instruction"/> with a
    /// <c>[turnId=…]</c> header. Incomplete current turn may append ephemeral mandates
    /// (rename review; Plan first-turn Explore).
    /// </summary>
    private static string FormatTurnUserContent(
        DysonAgentSession session,
        DysonAgentTurn turn,
        int zeroBasedIndex,
        bool incompleteCurrent)
    {
        var sb = new StringBuilder();
        sb.Append("[turnId=");
        sb.Append(turn.Id.ToString("D"));
        sb.AppendLine("]");
        sb.Append(turn.Instruction!);

        if (!incompleteCurrent)
            return sb.ToString();

        if (zeroBasedIndex == 0
            && string.Equals(session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(DysonAgentSystemPrompts.PlanFirstTurnMandate.Trim());
        }

        var oneBased = zeroBasedIndex + 1;
        if (DysonSessionInitialization.IsRenameReviewTurn(oneBased))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(DysonSessionInitialization.RenameSessionReviewMandate.Trim());
        }

        return sb.ToString();
    }

    private static string FormatAssistantReply(DysonAgentTurn turn)
    {
        if (string.IsNullOrEmpty(turn.AgentTitle))
            return turn.AssistantText ?? "";

        return $"# {turn.AgentTitle}\n\n{turn.AssistantText}";
    }
}

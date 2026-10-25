using System.Text;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Builds cache-friendly Completions messages / Responses input from local turn history.
/// Stable prefix first (system text), dynamic content last; never mutates
/// already-optimized turn sections. Tool schemas are tools[] only.
/// <see cref="DysonManagedSources.CliProxyClaude"/> also stamps Anthropic
/// <c>cache_control</c> ephemeral on the last tool, the current turn's last user text
/// part, and a builder-only anchor after each in-flight tool round. No other provider
/// gets those markers.
/// </summary>
public static class OpenAiCacheFriendlyTranscriptBuilder
{
    /// <summary>
    /// Emitted when a tool_call / function_call has no matching ResponseLog / round result
    /// (cancelled turn, abandoned WaitForSubagent, etc.). Keeps Completions/Responses transcripts paired.
    /// </summary>
    public const string IncompleteToolResultContent =
        "Tool call did not complete (cancelled or interrupted).";

    /// <summary>
    /// Prefix for compacted prior-tool summaries injected as <c>role: user</c> (not assistant).
    /// </summary>
    public const string CompactToolHistoryHarnessPrefix =
        "[Harness compacted prior tool results — historical summary only. Do not imitate this format; use native function/tool calls.]";

    /// <summary>
    /// Builder-only user line after each in-flight tool round for CLIProxy Claude.
    /// Not stored on the turn. The model sees this line. CLIProxy v7.3.15 copies
    /// <c>cache_control</c> from user content parts, not from <c>function_call_output</c>.
    /// </summary>
    public const string ClaudeCacheAnchorText =
        "[Harness: cache breakpoint after tool results. Not a user instruction.]";

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
        IReadOnlyList<DysonToolCallResult> Results,
        IReadOnlyList<JsonObject>? ReasoningItems = null);

    /// <summary>System text: mode prompt + reply format. Tool schemas are tools[] only.</summary>
    public static string BuildSystemText(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sb = new StringBuilder();
        sb.AppendLine(session.SystemPrompt);
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
        var claudeCache = UsesClaudeEphemeralCache(session);

        var systemText = BuildSystemText(session);
        var messages = new JsonArray();

        var systemContent = BuildTextContentParts(systemText, includeBreakpoints);
        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = systemContent,
        });

        AppendHistoryMessages(messages, session, excludeLastIfCurrent: false, claudeCache);

        var liveComments = LiveAnchoredComments(session);
        var nextComment = 0;
        var cumulativeCalls = 0;
        if (inFlightRounds is not null)
        {
            for (var i = 0; i < inFlightRounds.Count; i++)
            {
                // Data-URL / Files / non-image re-emit on every in-flight round so hop N+1
                // still starts with the bytes hop N sent. The first emission freezes the
                // URL, file id, or data URL; a later RemoteUrl / FileId write is ignored.
                // Finished turns keep data-URL / Files / non-image attachments off.
                AppendToolRoundCompletions(
                    messages,
                    inFlightRounds[i],
                    includeBinaryAttachments: true);
                if (claudeCache)
                    AppendClaudeCacheAnchor(messages, responses: false);
                cumulativeCalls += inFlightRounds[i].Calls.Count;
                nextComment = AppendLiveCommentsUpTo(messages, liveComments, nextComment, cumulativeCalls);
            }
        }

        AppendLiveCommentsUpTo(messages, liveComments, nextComment, int.MaxValue);

        // After in-flight rounds so harness follow-ups (e.g. SubmitSubagentReport nudge) land last.
        if (!string.IsNullOrEmpty(currentUserPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = FormatUserContent(currentUserPrompt, currentFilePaths),
            });
        }

        var tools = OpenAiCompatibleHttp.BuildToolsArray(session.McpPipeline);
        if (claudeCache)
            StampClaudeCacheOnLastTool(tools);

        return new BuiltCompletionsRequest(
            messages,
            tools,
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
            includeBreakpoints);
    }

    /// <summary>
    /// Full Responses rebuild (after compaction, new user turn, mid-loop fallback, or managed replay).
    /// Direct OpenAI: <c>store: true</c> and optional <paramref name="previousResponseId"/> for chaining.
    /// Managed/CLIProxy: <c>store: false</c>, never chains; replays reasoning → function_call → outputs.
    /// <paramref name="preserveReasoning"/>: finished turns also replay their stored reasoning items
    /// when the stored source matches the current <see cref="OpenAiCompatibleAgentProvider.ReasoningSourceTag"/>.
    /// </summary>
    public static BuiltResponsesRequest BuildResponsesFull(
        DysonAgentSession session,
        string? currentUserPrompt,
        IReadOnlyList<string>? currentFilePaths,
        IReadOnlyList<InFlightToolRound>? inFlightRounds = null,
        string? previousResponseId = null,
        bool preserveReasoning = false)
    {
        ArgumentNullException.ThrowIfNull(session);

        var (includeBreakpoints, store, chainPreviousId) = ResolveResponsesRequestFlags(session);
        var claudeCache = UsesClaudeEphemeralCache(session);
        var instructions = BuildSystemText(session);
        var input = new JsonArray();

        AppendHistoryAsResponsesInput(input, session, claudeCache, preserveReasoning);

        var liveComments = LiveAnchoredComments(session);
        var nextComment = 0;
        var cumulativeCalls = 0;
        if (inFlightRounds is not null)
        {
            for (var i = 0; i < inFlightRounds.Count; i++)
            {
                // Same prefix rule as Completions: every earlier round's binary stays,
                // including the URL / file id / data URL frozen on its first emission.
                AppendToolRoundResponses(
                    input,
                    inFlightRounds[i],
                    includeBinaryAttachments: true);
                if (claudeCache)
                    AppendClaudeCacheAnchor(input, responses: true);
                cumulativeCalls += inFlightRounds[i].Calls.Count;
                nextComment = AppendLiveCommentsUpTo(input, liveComments, nextComment, cumulativeCalls);
            }
        }

        AppendLiveCommentsUpTo(input, liveComments, nextComment, int.MaxValue);

        // After in-flight rounds so harness follow-ups (e.g. SubmitSubagentReport nudge) land last.
        if (!string.IsNullOrEmpty(currentUserPrompt))
        {
            input.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = FormatUserContent(currentUserPrompt, currentFilePaths),
            });
        }

        var tools = OpenAiCompatibleHttp.BuildResponsesToolsArray(session.McpPipeline);
        if (claudeCache)
            StampClaudeCacheOnLastTool(tools);

        return new BuiltResponsesRequest(
            instructions,
            input,
            tools,
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
            includeBreakpoints,
            PreviousResponseId: chainPreviousId ? previousResponseId : null,
            Store: store);
    }

    /// <summary>
    /// Responses delta within a tool loop (direct OpenAI only): <c>previous_response_id</c> + new
    /// function_call_output items. Always resends instructions/tools (spec: previous_response_id
    /// does not carry top-level instructions).
    /// </summary>
    public static BuiltResponsesRequest BuildResponsesDelta(
        DysonAgentSession session,
        string previousResponseId,
        IReadOnlyList<DysonToolCallResult> newResults)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousResponseId);
        ArgumentNullException.ThrowIfNull(newResults);

        var (includeBreakpoints, _, _) = ResolveResponsesRequestFlags(session);

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
                AppendResponsesBinaryAttachment(input, attachment, captureWire: true);
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

    /// <summary>
    /// Direct OpenAI: store+chain. Managed: store false, no previous_response_id.
    /// Non-OpenAI stubs (tests): keep store+chain so existing fixtures stay valid.
    /// </summary>
    private static (bool IncludeBreakpoints, bool Store, bool ChainPreviousId) ResolveResponsesRequestFlags(
        DysonAgentSession session)
    {
        if (session.Provider is OpenAiCompatibleAgentProvider oai)
        {
            var chain = OpenAiCompatibleHttp.SupportsResponsesServerChaining(oai);
            return (
                OpenAiCompatibleHttp.SupportsExplicitPromptCache(oai),
                Store: chain,
                ChainPreviousId: chain);
        }

        return (IncludeBreakpoints: false, Store: true, ChainPreviousId: true);
    }

    /// <summary>Anthropic ephemeral breakpoints. CLIProxy Claude only; other wires 400 on this field.</summary>
    private static bool UsesClaudeEphemeralCache(DysonAgentSession session) =>
        session.Provider is OpenAiCompatibleAgentProvider provider
        && string.Equals(
            provider.ManagedSource,
            DysonManagedSources.CliProxyClaude,
            StringComparison.Ordinal);

    private static JsonObject EphemeralCacheControl() =>
        new() { ["type"] = "ephemeral" };

    private static void StampClaudeCacheOnLastTool(JsonArray tools)
    {
        if (tools.Count == 0 || tools[^1] is not JsonObject tool)
            return;

        tool["cache_control"] = EphemeralCacheControl();
    }

    /// <summary>
    /// ponytail: cache_control stays on every prior anchor, not only the latest, so hop N+1
    /// bytes match hop N. Ceiling: Anthropic's 4-breakpoint cap on a long tool loop; a proxy
    /// that can pin the tool tail without a per-round content part would retire these anchors.
    /// </summary>
    private static void AppendClaudeCacheAnchor(JsonArray target, bool responses)
    {
        var textType = responses ? "input_text" : "text";
        target.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray { ClaudeTextPart(textType, ClaudeCacheAnchorText) },
        });
    }

    private static JsonNode TurnUserContent(
        DysonAgentSession session,
        DysonAgentTurn turn,
        int index,
        bool responses,
        bool stampClaude,
        bool captureWire)
    {
        var content = responses
            ? BuildResponsesTurnUserContent(session, turn, index, captureWire)
            : BuildCompletionsTurnUserContent(session, turn, index, captureWire);
        return stampClaude ? WithClaudeCacheOnLastTextPart(content, responses) : content;
    }

    /// <summary>
    /// Puts <c>cache_control</c> on the last text / input_text part. A plain string becomes one part
    /// so the marker is identical on every later hop (the part is not added only after tools exist).
    /// </summary>
    private static JsonNode WithClaudeCacheOnLastTextPart(JsonNode content, bool responses)
    {
        var textType = responses ? "input_text" : "text";
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
            return new JsonArray { ClaudeTextPart(textType, text) };

        if (content is not JsonArray parts)
            return content;

        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (parts[i] is not JsonObject part)
                continue;
            var type = part["type"]?.GetValue<string>();
            if (type is not ("text" or "input_text"))
                continue;

            part["cache_control"] = EphemeralCacheControl();
            return parts;
        }

        return content;
    }

    private static JsonObject ClaudeTextPart(string textType, string text) =>
        new()
        {
            ["type"] = textType,
            ["text"] = text,
            ["cache_control"] = EphemeralCacheControl(),
        };

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
        bool excludeLastIfCurrent,
        bool claudeCache)
    {
        var turns = session.Turns;
        var incompleteIndex = FindIncompleteCurrentIndex(session);
        var count = turns.Count;
        for (var i = 0; i < count; i++)
        {
            if (excludeLastIfCurrent && i == incompleteIndex)
                break;

            var turn = turns[i];
            if (turn.Kind is DysonAgentTurnKind.DisplayInfo or DysonAgentTurnKind.WorktreeCreating)
                continue;

            if (turn.IsExcludedFromContext)
            {
                AppendExcludedTurn(messages, turn, i == incompleteIndex);
                continue;
            }

            if (turn.Kind == DysonAgentTurnKind.ModeSwitch)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatModeSwitchHarnessUserMessage(turn),
                });
                continue;
            }

            // Summarized turns: compact stub only (same omission pattern as drop for body/tools).
            if (DysonTurnSummarizer.HasSummary(turn))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = DysonTurnSummarizer.FormatSummaryStub(turn),
                });
                continue;
            }

            // User content keeps rename / Plan mandates on the turn that earned them.
            // In-progress tool rounds come from inFlightRounds. PlanResult may append after the live turn.
            var incompleteCurrent = i == incompleteIndex;
            if (HasTurnUserContent(turn))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = TurnUserContent(
                        session,
                        turn,
                        i,
                        responses: false,
                        stampClaude: claudeCache && incompleteCurrent,
                        captureWire: incompleteCurrent),
                });
            }

            AppendContextFileUserMessages(messages, turn);

            var comments = DysonInjectedUserComments.From(turn.ReasoningLog);
            DysonInjectedUserComments.AppendMessages(
                messages,
                comments.Where(c => c.DeliveredAfterToolCalls == 0));

            if (incompleteCurrent)
                continue;

            if (turn.ToolHistoryOptimized && !string.IsNullOrEmpty(turn.CompactToolHistory))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatCompactToolHistoryUserContent(turn.CompactToolHistory),
                });
                // Position inside compacted tools is not recoverable: comments follow the stub.
                DysonInjectedUserComments.AppendMessages(
                    messages,
                    comments.Where(c => c.DeliveredAfterToolCalls != 0));
                continue;
            }

            // History: data-URL / Files stay off; RemoteUrl images still re-emit.
            AppendTurnToolsWithComments(messages, turn, comments, responses: false, replayReasoning: false);

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
        DysonAgentSession session,
        bool claudeCache,
        bool preserveReasoning)
    {
        var currentSource = (session.Provider as OpenAiCompatibleAgentProvider)?.ReasoningSourceTag;
        var turns = session.Turns;
        var incompleteIndex = FindIncompleteCurrentIndex(session);
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (turn.Kind is DysonAgentTurnKind.DisplayInfo or DysonAgentTurnKind.WorktreeCreating)
                continue;

            if (turn.IsExcludedFromContext)
            {
                AppendExcludedTurn(input, turn, i == incompleteIndex);
                continue;
            }

            if (turn.Kind == DysonAgentTurnKind.ModeSwitch)
            {
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatModeSwitchHarnessUserMessage(turn),
                });
                continue;
            }

            if (DysonTurnSummarizer.HasSummary(turn))
            {
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = DysonTurnSummarizer.FormatSummaryStub(turn),
                });
                continue;
            }

            var incompleteCurrent = i == incompleteIndex;
            if (HasTurnUserContent(turn))
            {
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = TurnUserContent(
                        session,
                        turn,
                        i,
                        responses: true,
                        stampClaude: claudeCache && incompleteCurrent,
                        captureWire: incompleteCurrent),
                });
            }

            AppendContextFileUserMessages(input, turn);

            var comments = DysonInjectedUserComments.From(turn.ReasoningLog);
            DysonInjectedUserComments.AppendMessages(
                input,
                comments.Where(c => c.DeliveredAfterToolCalls == 0));

            if (incompleteCurrent)
                continue;

            if (turn.ToolHistoryOptimized && !string.IsNullOrEmpty(turn.CompactToolHistory))
            {
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = FormatCompactToolHistoryUserContent(turn.CompactToolHistory),
                });
                DysonInjectedUserComments.AppendMessages(
                    input,
                    comments.Where(c => c.DeliveredAfterToolCalls != 0));
                continue;
            }

            // Foreign blobs (another provider source) would be rejected; drop them.
            var replayReasoning = preserveReasoning
                && string.Equals(turn.ReasoningItemsSource, currentSource, StringComparison.Ordinal);
            AppendTurnToolsWithComments(input, turn, comments, responses: true, replayReasoning);

            if (!string.IsNullOrEmpty(turn.AssistantText))
            {
                if (replayReasoning && turn.FinalReasoningItems is { Count: > 0 } finalReasoning)
                {
                    foreach (var item in finalReasoning)
                        input.Add(item.DeepClone());
                }

                input.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = FormatAssistantReply(turn),
                });
            }
        }
    }

    /// <summary>
    /// DropTurnContext / FullSummarize exclude the turn body, never its user comments: they are
    /// user instructions. Emitted as marked messages at the turn's slot. For the in-flight turn
    /// only anchor-0 comments go here (later anchors come from <see cref="LiveAnchoredComments"/>).
    /// </summary>
    private static void AppendExcludedTurn(JsonArray target, DysonAgentTurn turn, bool incompleteCurrent)
    {
        // UI /summarize (and any leftover ContextSummary) still emits the stub, which already carries comments.
        // A DropTurnContext resume is not stored on the excluded turn.
        if (DysonTurnSummarizer.HasSummary(turn))
        {
            target.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = DysonTurnSummarizer.FormatSummaryStub(turn),
            });
            return;
        }

        AppendExcludedTurnComments(target, turn, incompleteCurrent);
    }

    private static void AppendExcludedTurnComments(JsonArray target, DysonAgentTurn turn, bool incompleteCurrent)
    {
        var comments = DysonInjectedUserComments.From(turn.ReasoningLog);
        DysonInjectedUserComments.AppendMessages(
            target,
            incompleteCurrent ? comments.Where(c => c.DeliveredAfterToolCalls == 0) : comments);
    }

    /// <summary>
    /// In-flight current turn: comments delivered after one or more tool calls, in anchor order.
    /// Anchor 0 sits in history right after the turn instruction; null anchors are still pending
    /// (they drain and get stamped at the next round start).
    /// </summary>
    private static List<DysonReasoningSegment> LiveAnchoredComments(DysonAgentSession session)
    {
        var turns = session.Turns;
        var index = FindIncompleteCurrentIndex(session);
        if (index < 0)
            return [];

        return DysonInjectedUserComments.From(turns[index].ReasoningLog)
            .Where(c => c.DeliveredAfterToolCalls is > 0)
            .OrderBy(c => c.DeliveredAfterToolCalls)
            .ToList();
    }

    /// <summary>
    /// Emits live comments whose anchor is reached by <paramref name="cumulativeCalls"/>, so a comment
    /// lands right after the round results the model had seen when it was delivered. Later rounds
    /// only append after it (prefix-stable). Returns the next unemitted index.
    /// </summary>
    private static int AppendLiveCommentsUpTo(
        JsonArray target,
        List<DysonReasoningSegment> comments,
        int next,
        int cumulativeCalls)
    {
        var start = next;
        while (next < comments.Count && comments[next].DeliveredAfterToolCalls <= cumulativeCalls)
            next++;

        DysonInjectedUserComments.AppendMessages(target, comments.Skip(start).Take(next - start));
        return next;
    }

    /// <summary>
    /// Completed turn tools. Recorded <see cref="DysonAgentTurn.ToolRoundCallCounts"/> that sum to
    /// <see cref="DysonAgentTurn.ToolCalls"/> replay each round through the in-flight appenders,
    /// with that round's stored reasoning items when <paramref name="replayReasoning"/> (Responses
    /// only). Otherwise one chunk per comment-anchor span (legacy, no reasoning). Anchor 0 is
    /// already emitted. Null anchors follow all tools, before the final assistant text.
    /// </summary>
    private static void AppendTurnToolsWithComments(
        JsonArray target,
        DysonAgentTurn turn,
        List<DysonReasoningSegment> comments,
        bool responses,
        bool replayReasoning)
    {
        if (TryAppendRecordedToolRounds(target, turn, comments, responses, replayReasoning))
            return;

        var calls = turn.ToolCalls;
        var start = 0;
        foreach (var group in comments
                     .Where(c => c.DeliveredAfterToolCalls is > 0)
                     .GroupBy(c => c.DeliveredAfterToolCalls!.Value)
                     .OrderBy(g => g.Key))
        {
            var end = Math.Min(group.Key, calls.Count);
            AppendHistoryToolChunk(target, turn, start, end, responses);
            start = Math.Max(start, end);
            DysonInjectedUserComments.AppendMessages(target, group);
        }

        AppendHistoryToolChunk(target, turn, start, calls.Count, responses);
        DysonInjectedUserComments.AppendMessages(
            target,
            comments.Where(c => c.DeliveredAfterToolCalls is null));
    }

    /// <summary>
    /// Replays recorded rounds. False when counts are missing or do not sum to the call list.
    /// </summary>
    private static bool TryAppendRecordedToolRounds(
        JsonArray target,
        DysonAgentTurn turn,
        List<DysonReasoningSegment> comments,
        bool responses,
        bool replayReasoning)
    {
        var counts = turn.ToolRoundCallCounts;
        if (counts.Count == 0)
            return false;

        var sum = 0;
        foreach (var count in counts)
        {
            if (count < 0)
                return false;

            sum += count;
        }

        if (sum != turn.ToolCalls.Count)
            return false;

        var anchored = comments
            .Where(c => c.DeliveredAfterToolCalls is > 0)
            .OrderBy(c => c.DeliveredAfterToolCalls)
            .ToList();
        var next = 0;
        var offset = 0;
        var cumulative = 0;
        for (var roundIndex = 0; roundIndex < counts.Count; roundIndex++)
        {
            var count = counts[roundIndex];
            if (count > 0)
            {
                var slice = turn.ToolCalls.GetRange(offset, count);
                var reasoning = replayReasoning && responses
                    ? turn.ToolRoundReasoningItems.ElementAtOrDefault(roundIndex)
                    : null;
                var round = new InFlightToolRound(slice, ResultsForCalls(turn, slice), reasoning);
                if (responses)
                    AppendToolRoundResponses(target, round, includeBinaryAttachments: false);
                else
                    AppendToolRoundCompletions(target, round, includeBinaryAttachments: false);
            }

            offset += count;
            cumulative += count;
            next = AppendLiveCommentsUpTo(target, anchored, next, cumulative);
        }

        AppendLiveCommentsUpTo(target, anchored, next, int.MaxValue);
        DysonInjectedUserComments.AppendMessages(
            target,
            comments.Where(c => c.DeliveredAfterToolCalls is null));
        return true;
    }

    private static List<DysonToolCallResult> ResultsForCalls(
        DysonAgentTurn turn,
        IReadOnlyList<DysonToolCall> calls)
    {
        var byId = IndexResultsByCallId(turn.ResponseLog);
        var results = new List<DysonToolCallResult>(calls.Count);
        foreach (var call in calls)
        {
            if (byId.TryGetValue(call.CallId, out var result))
                results.Add(result);
        }

        return results;
    }

    private static void AppendHistoryToolChunk(
        JsonArray target,
        DysonAgentTurn turn,
        int start,
        int end,
        bool responses)
    {
        if (end <= start)
            return;

        var chunk = turn.ToolCalls.GetRange(start, end - start);
        if (responses)
            AppendToolCallBlockResponses(target, chunk, turn.ResponseLog, includeBinaryAttachments: false);
        else
            AppendToolCallBlockCompletions(target, chunk, turn.ResponseLog, includeBinaryAttachments: false);
    }

    private static void AppendToolRoundCompletions(
        JsonArray messages,
        InFlightToolRound round,
        bool includeBinaryAttachments) =>
        AppendToolCallBlockCompletions(messages, round.Calls, round.Results, includeBinaryAttachments);

    /// <summary>Assistant <c>tool_calls</c> message + consecutive paired <c>role: tool</c> results.</summary>
    private static void AppendToolCallBlockCompletions(
        JsonArray messages,
        IReadOnlyList<DysonToolCall> calls,
        IEnumerable<DysonToolCallResult> results,
        bool includeBinaryAttachments)
    {
        var toolCalls = new JsonArray();
        foreach (var call in calls)
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
            calls,
            results,
            includeBinaryAttachments);
    }

    private static void AppendToolRoundResponses(
        JsonArray input,
        InFlightToolRound round,
        bool includeBinaryAttachments)
    {
        // Stateless / full-replay order: reasoning → function_call → function_call_output.
        if (round.ReasoningItems is { Count: > 0 })
        {
            foreach (var item in round.ReasoningItems)
                input.Add(item.DeepClone());
        }

        AppendToolCallBlockResponses(input, round.Calls, round.Results, includeBinaryAttachments);
    }

    /// <summary><c>function_call</c> items + consecutive paired <c>function_call_output</c> items.</summary>
    private static void AppendToolCallBlockResponses(
        JsonArray input,
        IReadOnlyList<DysonToolCall> calls,
        IEnumerable<DysonToolCallResult> results,
        bool includeBinaryAttachments)
    {
        foreach (var call in calls)
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
            calls,
            results,
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

        foreach (var call in calls)
        {
            if (byCallId.TryGetValue(call.CallId, out var result)
                && result is { IsError: false, BinaryAttachment: { } attachment }
                && ShouldEmitBinaryAttachment(attachment, includeBinaryAttachments))
            {
                AppendCompletionsBinaryAttachment(messages, attachment, includeBinaryAttachments);
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

        foreach (var call in calls)
        {
            if (byCallId.TryGetValue(call.CallId, out var result)
                && result is { IsError: false, BinaryAttachment: { } attachment }
                && ShouldEmitBinaryAttachment(attachment, includeBinaryAttachments))
            {
                AppendResponsesBinaryAttachment(input, attachment, includeBinaryAttachments);
            }
        }
    }

    /// <summary>
    /// RemoteUrl images emit on every rebuild (in-flight + history). Data-URL / Files / non-image
    /// emit on every in-flight round (<paramref name="includeBinaryAttachments"/>) and stay off
    /// for finished turns. A frozen in-flight wire keeps that choice: a RemoteUrl written after
    /// a data-URL emission does not start appearing in history.
    /// </summary>
    private static bool ShouldEmitBinaryAttachment(
        DysonBinaryAttachment attachment,
        bool includeBinaryAttachments) =>
        includeBinaryAttachments
        || (attachment.IsImage
            && !string.IsNullOrWhiteSpace(attachment.GetReplayWire(capture: false).RemoteUrl));

    /// <summary>
    /// Completions: short tool ack already emitted; follow with a user multimodal message.
    /// Images: nested <c>image_url: { url, detail }</c> (RemoteUrl HTTPS or data URL; no
    /// <c>filename</c>, no <c>file_id</c>). Non-images: <c>file.filename</c> + <c>file_data</c>.
    /// </summary>
    private static void AppendCompletionsBinaryAttachment(
        JsonArray messages,
        DysonBinaryAttachment attachment,
        bool captureWire)
    {
        var wire = attachment.GetReplayWire(captureWire);
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
                    ["url"] = ImageUrlFromWire(attachment, wire.RemoteUrl),
                    ["detail"] = "auto",
                },
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
                    ["file_data"] = BuildDataUrl(attachment),
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
    /// Responses: follow function_call_output with input_image / input_file.
    /// Images with RemoteUrl use <c>image_url</c> (HTTPS) and skip <c>file_id</c>.
    /// Else prefer <c>file_id</c> when present; else data URL / file_data. Never <c>filename</c> on images.
    /// </summary>
    private static void AppendResponsesBinaryAttachment(
        JsonArray input,
        DysonBinaryAttachment attachment,
        bool captureWire)
    {
        var wire = attachment.GetReplayWire(captureWire);
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = $"Loaded binary attachment: {attachment.FileName}",
            },
        };

        if (attachment.IsImage)
            AppendResponsesInputImage(parts, attachment, wire);
        else if (!string.IsNullOrEmpty(wire.FileId))
        {
            parts.Add(new JsonObject
            {
                ["type"] = "input_file",
                ["file_id"] = wire.FileId,
            });
        }
        else
        {
            parts.Add(new JsonObject
            {
                ["type"] = "input_file",
                ["filename"] = attachment.FileName,
                ["file_data"] = BuildDataUrl(attachment),
            });
        }

        input.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = parts,
        });
    }

    private static void AppendResponsesInputImage(
        JsonArray parts,
        DysonBinaryAttachment attachment,
        (string? RemoteUrl, string? FileId) wire)
    {
        if (!string.IsNullOrWhiteSpace(wire.RemoteUrl))
        {
            parts.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = wire.RemoteUrl,
                ["detail"] = "auto",
            });
            return;
        }

        if (!string.IsNullOrEmpty(wire.FileId))
        {
            parts.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["file_id"] = wire.FileId,
                ["detail"] = "auto",
            });
            return;
        }

        parts.Add(new JsonObject
        {
            ["type"] = "input_image",
            ["image_url"] = BuildDataUrl(attachment),
            ["detail"] = "auto",
        });
    }

    /// <summary>Frozen or live HTTPS RemoteUrl when set; otherwise a data URL. Images only.</summary>
    private static string ImageUrlFromWire(DysonBinaryAttachment attachment, string? remoteUrl) =>
        string.IsNullOrWhiteSpace(remoteUrl) ? BuildDataUrl(attachment) : remoteUrl;

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

        var returned = DysonJsonDynamicToolchainSchema.TryFormatReturnedToolResultForModel(
            result.ToolName,
            result.Content,
            result.IsError);
        if (returned is not null)
            return returned;

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
    private static int FindIncompleteCurrentIndex(DysonAgentSession session)
    {
        var turns = session.Turns;
        // AssistantText may already hold a DropTurnContext resume while tools are still in flight.
        // Keep that turn incomplete so history does not also emit the in-flight tool rounds.
        var inFlight = session.InFlightPromptTurn;
        if (inFlight is not null)
        {
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(turns[i], inFlight) || turns[i].Id == inFlight.Id)
                    return i;
            }
        }

        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (IsTranscriptChromeKind(turns[i].Kind))
                continue;
            if (string.IsNullOrEmpty(turns[i].AssistantText))
                return i;
            break;
        }

        return -1;
    }

    /// <summary>
    /// History turns send <see cref="DysonAgentTurn.Instruction"/> with a
    /// <c>[turnId=…]</c> header. Mid-turn user comments are never spliced here: they are
    /// separate <see cref="DysonInjectedUserComments"/> messages at their delivery anchor.
    /// A rename-review or first Plan-stint turn keeps that mandate on this user message
    /// after the turn completes. It is not copied onto Instruction.
    /// After the instruction, appends <see cref="DysonAgentTurn.HiddenInstruction"/>
    /// (local paths only) and live <c>Attached urls:</c> lines from
    /// <see cref="DysonAgentTurn.UserImages"/>.
    /// </summary>
    private static string FormatTurnUserContent(
        DysonAgentSession session,
        DysonAgentTurn turn,
        int zeroBasedIndex,
        bool captureWire)
    {
        var sb = new StringBuilder();
        sb.Append("[turnId=");
        sb.Append(turn.Id.ToString("D"));
        sb.AppendLine("]");
        if (!string.IsNullOrEmpty(turn.Instruction))
            sb.Append(turn.Instruction);

        // Column is already "Attached paths:" lines. Do not wrap it again or add URLs.
        if (!string.IsNullOrWhiteSpace(turn.HiddenInstruction))
        {
            if (sb.Length > 0 && sb[^1] != '\n')
                sb.AppendLine();
            sb.AppendLine();
            sb.Append(turn.HiddenInstruction.Trim());
        }

        AppendLiveImageUrls(sb, turn, captureWire);

        if (turn.UserImages.Count > 0)
        {
            if (sb.Length > 0 && sb[^1] != '\n')
                sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Attached images:");
            foreach (var image in turn.UserImages)
                sb.AppendLine($"- {image.FileName}");
        }

        if (ShouldAppendPlanFirstTurnMandate(
                session, session.Turns, zeroBasedIndex, turn))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(DysonAgentSystemPrompts.PlanFirstTurnMandate.Trim());
        }

        if (DysonSessionInitialization.IsRenameReviewTurn(
                ChromeSkippedOneBasedIndex(session.Turns, zeroBasedIndex)))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(DysonSessionInitialization.RenameSessionReviewMandate.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Instruction, local-path column, or images. Empty instruction still emits when either attachment is set.
    /// </summary>
    private static bool HasTurnUserContent(DysonAgentTurn turn) =>
        !string.IsNullOrEmpty(turn.Instruction)
        || !string.IsNullOrWhiteSpace(turn.HiddenInstruction)
        || turn.UserImages.Count > 0;

    /// <summary>
    /// <c>Attached urls:</c> from each user image whose replay URL is set. The in-flight turn
    /// freezes that URL on first emission. A finished turn that was never frozen still follows
    /// the attachment's current <see cref="DysonBinaryAttachment.RemoteUrl"/>. Skips images with
    /// no URL. Does not write the URL into <see cref="DysonAgentTurn.HiddenInstruction"/> or
    /// clear <see cref="DysonBinaryAttachment.Base64Data"/>.
    /// </summary>
    private static void AppendLiveImageUrls(StringBuilder sb, DysonAgentTurn turn, bool captureWire)
    {
        var wroteHeader = false;
        foreach (var image in turn.UserImages)
        {
            var url = image.GetReplayWire(captureWire).RemoteUrl;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (!wroteHeader)
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("Attached urls:");
                wroteHeader = true;
            }

            sb.Append("- ");
            sb.Append(image.FileName);
            sb.Append(' ');
            sb.AppendLine(url);
        }
    }

    /// <summary>
    /// DisplayInfo / WorktreeCreating / ModeSwitch / PlanResult occupy list slots but are not eligible
    /// prompt turns (same set as <see cref="FindIncompleteCurrentIndex"/>).
    /// </summary>
    private static bool IsTranscriptChromeKind(DysonAgentTurnKind kind) =>
        kind is DysonAgentTurnKind.DisplayInfo
            or DysonAgentTurnKind.WorktreeCreating
            or DysonAgentTurnKind.ModeSwitch
            or DysonAgentTurnKind.PlanResult;

    /// <summary>
    /// 1-based count of non-chrome turns in <paramref name="turns"/>[0..<paramref name="currentIndex"/>].
    /// </summary>
    private static int ChromeSkippedOneBasedIndex(
        IReadOnlyList<DysonAgentTurn> turns,
        int currentIndex)
    {
        var count = 0;
        var last = Math.Min(currentIndex, turns.Count - 1);
        for (var i = 0; i <= last; i++)
        {
            if (IsTranscriptChromeKind(turns[i].Kind))
                continue;
            count++;
        }

        return count;
    }

    /// <summary>
    /// True for the first Normal/InitializeSession prompt in the current Plan stint,
    /// including after that turn completes.
    /// ModeSwitch is inspected (To=Plan starts a stint); DisplayInfo / WorktreeCreating / PlanResult are skipped.
    /// </summary>
    private static bool ShouldAppendPlanFirstTurnMandate(
        DysonAgentSession session,
        IReadOnlyList<DysonAgentTurn> turns,
        int currentIndex,
        DysonAgentTurn turn)
    {
        if (!string.Equals(session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsEligiblePlanStintUserTurn(turn))
            return false;

        for (var i = currentIndex - 1; i >= 0; i--)
        {
            var prior = turns[i];
            if (prior.Kind is DysonAgentTurnKind.DisplayInfo
                or DysonAgentTurnKind.WorktreeCreating
                or DysonAgentTurnKind.PlanResult)
                continue;

            if (prior.Kind == DysonAgentTurnKind.ModeSwitch
                && TryParseModeSwitch(prior, out _, out var to)
                && string.Equals(to, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase))
                return true;

            if (IsEligiblePlanStintUserTurn(prior))
                return false;
        }

        return true;
    }

    private static bool IsEligiblePlanStintUserTurn(DysonAgentTurn turn) =>
        turn.Kind is DysonAgentTurnKind.Normal or DysonAgentTurnKind.InitializeSession
        && !string.IsNullOrEmpty(turn.Instruction);

    /// <summary>
    /// Completions user content: plain string when no images; multimodal parts when
    /// <see cref="DysonAgentTurn.UserImages"/> is non-empty.
    /// </summary>
    private static JsonNode BuildCompletionsTurnUserContent(
        DysonAgentSession session,
        DysonAgentTurn turn,
        int zeroBasedIndex,
        bool captureWire)
    {
        var text = FormatTurnUserContent(session, turn, zeroBasedIndex, captureWire);
        if (turn.UserImages.Count == 0)
            return text;

        var parts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            },
        };

        foreach (var image in turn.UserImages)
        {
            var wire = image.GetReplayWire(captureWire);
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = ImageUrlFromWire(image, wire.RemoteUrl),
                    ["detail"] = "auto",
                },
            });
        }

        return parts;
    }

    /// <summary>
    /// Responses user content: plain string when no images; <c>input_text</c> + <c>input_image</c>
    /// parts when <see cref="DysonAgentTurn.UserImages"/> is non-empty.
    /// </summary>
    private static JsonNode BuildResponsesTurnUserContent(
        DysonAgentSession session,
        DysonAgentTurn turn,
        int zeroBasedIndex,
        bool captureWire)
    {
        var text = FormatTurnUserContent(session, turn, zeroBasedIndex, captureWire);
        if (turn.UserImages.Count == 0)
            return text;

        var parts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = text,
            },
        };

        foreach (var image in turn.UserImages)
            AppendResponsesInputImage(parts, image, image.GetReplayWire(captureWire));

        return parts;
    }

    /// <summary>
    /// Emits one user message per context file after the turn instruction so the model sees full
    /// markdown without dumping it into the visible prompt UI.
    /// </summary>
    private static void AppendContextFileUserMessages(JsonArray messages, DysonAgentTurn turn)
    {
        foreach (var entry in turn.ContextFiles)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"[{entry.Kind}: {entry.DisplayName}]\n\n{entry.MarkdownContent}",
            });
        }
    }

    /// <summary>
    /// Short harness boundary for <see cref="DysonAgentTurnKind.ModeSwitch"/>
    /// (<see cref="DysonAgentTurn.Instruction"/> = <c>From→To</c>).
    /// </summary>
    private static string FormatModeSwitchHarnessUserMessage(DysonAgentTurn turn)
    {
        var from = "?";
        var to = "?";
        if (TryParseModeSwitch(turn, out var parsedFrom, out var parsedTo))
        {
            if (!string.IsNullOrEmpty(parsedFrom))
                from = parsedFrom;
            if (!string.IsNullOrEmpty(parsedTo))
                to = parsedTo;
        }

        return
            $"[Harness: agent mode switched from {from} to {to}. Follow the current system instructions for {to} mode from this point on.]";
    }

    /// <summary>
    /// Parses <see cref="DysonAgentTurn.Instruction"/> <c>From→To</c> on a ModeSwitch turn.
    /// </summary>
    private static bool TryParseModeSwitch(DysonAgentTurn turn, out string from, out string to)
    {
        from = "";
        to = "";
        var instruction = turn.Instruction ?? "";
        var arrow = instruction.IndexOf('→');
        if (arrow <= 0 || arrow >= instruction.Length - 1)
            return false;

        from = instruction[..arrow].Trim();
        to = instruction[(arrow + 1)..].Trim();
        return true;
    }

    private static string FormatAssistantReply(DysonAgentTurn turn)
    {
        if (string.IsNullOrEmpty(turn.AgentTitle))
            return turn.AssistantText ?? "";

        return $"# {turn.AgentTitle}\n\n{turn.AssistantText}";
    }

    private static string FormatCompactToolHistoryUserContent(string compactToolHistory) =>
        $"{CompactToolHistoryHarnessPrefix}\n\n{compactToolHistory}";
}

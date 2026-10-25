using System.Text;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// One-shot Completions summarizer for turn context compression (UI /summarize worker).
/// Cap each /summarize result at <see cref="MaxSummaryTokens"/> (tiktoken); the live-compaction
/// fallback (<see cref="SummarizeForCompactionAsync"/>) is not capped.
/// </summary>
public static class DysonTurnSummarizer
{
    public const int MaxSummaryTokens = 2_000;
    public const int FallbackExcerptChars = 1_500;

    /// <summary>True when a non-empty <see cref="DysonAgentTurn.ContextSummary"/> is set.</summary>
    public static bool HasSummary(DysonAgentTurn turn) =>
        turn is not null && !string.IsNullOrWhiteSpace(turn.ContextSummary);

    /// <summary>
    /// Builds compact turn text for the summarizer (instruction, assistant, tool log, user comments).
    /// </summary>
    public static string FormatTurnBody(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(turn.Instruction))
        {
            sb.AppendLine("Instruction:");
            sb.AppendLine(turn.Instruction);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(turn.AssistantText))
        {
            sb.AppendLine("Assistant:");
            sb.AppendLine(turn.AssistantText);
            sb.AppendLine();
        }

        if (turn.ToolHistoryOptimized && !string.IsNullOrEmpty(turn.CompactToolHistory))
        {
            sb.AppendLine("Tools (compact):");
            sb.AppendLine(turn.CompactToolHistory);
        }
        else
        {
            var tools = turn.FormatResponseLog();
            if (!string.IsNullOrWhiteSpace(tools))
            {
                sb.AppendLine("Tools:");
                sb.AppendLine(tools);
            }
        }

        var comments = DysonInjectedUserComments.FormatPlainText(turn.ReasoningLog);
        if (!string.IsNullOrEmpty(comments))
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine("User comments:");
            sb.Append(comments);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// One-shot Completions summarize. Returns summary text, or a truncated excerpt on failure.
    /// When the reply includes a usage object, <paramref name="recordUsage"/> is invoked once.
    /// A missing usage object (including failed calls that return fallback text) does not invoke it.
    /// </summary>
    public static async Task<string> SummarizeAsync(
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        DysonAgentTurn turn,
        string? reason = null,
        IDysonTokenCounter? tokens = null,
        CancellationToken cancellationToken = default,
        Func<DysonParsedUsage, CancellationToken, Task>? recordUsage = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(turn);

        tokens ??= new DysonTiktokenTokenCounter();
        var raw = FormatTurnBody(turn);
        if (string.IsNullOrWhiteSpace(raw))
            return "(empty turn)";

        var summary = await RequestSummaryAsync(
                provider, http, raw, reason, DysonTurnSummarizerPrompt.System, tokens, recordUsage, cancellationToken)
            .ConfigureAwait(false);
        return summary.IsError
            ? Fallback(raw, summary.Error)
            : DysonWebSearchSummarizer.TrimToMaxTokens(summary.Value, tokens, MaxSummaryTokens);
    }

    /// <summary>
    /// Live-compaction fallback when the full summary request is over the model window: the same one-shot
    /// request with <see cref="DysonTurnSummarizerPrompt.CompactionSystem"/>. The reply is not trimmed
    /// (length is a prompt guideline only), and a failed request is an error, not an excerpt.
    /// </summary>
    public static async Task<Result<string, string>> SummarizeForCompactionAsync(
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        DysonAgentTurn history,
        string? reason = null,
        IDysonTokenCounter? tokens = null,
        CancellationToken cancellationToken = default,
        Func<DysonParsedUsage, CancellationToken, Task>? recordUsage = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(history);

        var raw = FormatTurnBody(history);
        if (string.IsNullOrWhiteSpace(raw))
            return Result<string, string>.AsError("Turn summarizer: no history to summarize.");

        return await RequestSummaryAsync(
                provider,
                http,
                raw,
                reason,
                DysonTurnSummarizerPrompt.CompactionSystem,
                tokens ?? new DysonTiktokenTokenCounter(),
                recordUsage,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<string, string>> RequestSummaryAsync(
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        string raw,
        string? reason,
        string system,
        IDysonTokenCounter tokens,
        Func<DysonParsedUsage, CancellationToken, Task>? recordUsage,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = DysonTurnSummarizerPrompt.FormatUserMessage(raw, reason);
            user = DysonWebSearchSummarizer.TrimToMaxTokens(user, tokens, 32_000);

            var baseUrl = OpenAiCompatibleHttp.NormalizeBaseUrl(provider.BaseUrl);
            var url = $"{baseUrl}/chat/completions";
            var body = new JsonObject
            {
                ["model"] = provider.Slug,
                ["stream"] = false,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = system,
                    },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = user,
                    },
                },
            };

            var response = await OpenAiCompatibleHttp
                .SendJsonAsync(http, HttpMethod.Post, url, provider.ApiKey, body, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsError)
                return Result<string, string>.AsError(response.Error);

            var parsed = OpenAiCompletionsClient.Parse(response.Value);
            if (parsed.IsError)
                return Result<string, string>.AsError(parsed.Error);

            if (parsed.Value.Usage is { } usage && recordUsage is not null)
                await recordUsage(usage, cancellationToken).ConfigureAwait(false);

            var summary = parsed.Value.Content?.Trim() ?? "";
            return string.IsNullOrWhiteSpace(summary)
                ? Result<string, string>.AsError("Turn summarizer returned empty content.")
                : Result<string, string>.AsValue(summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError(ex.Message);
        }
    }

    /// <summary>
    /// Transcript stub: turnId header + summary body, then persisted UserComment
    /// blocks so comments survive summarization or a DropTurnContext resume (HasSummary emits only this stub).
    /// </summary>
    public static string FormatSummaryStub(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var sb = new StringBuilder();
        sb.Append("[turnId=");
        sb.Append(turn.Id.ToString("D"));
        sb.AppendLine("]");
        sb.Append("[contextSummary]");
        sb.AppendLine();
        sb.Append(turn.ContextSummary?.Trim() ?? "");
        var comments = DysonInjectedUserComments.FormatPlainText(turn.ReasoningLog);
        if (!string.IsNullOrEmpty(comments))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(comments);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Fallback(string raw, string error)
    {
        var excerpt = raw ?? "";
        if (excerpt.Length > FallbackExcerptChars)
            excerpt = excerpt[..FallbackExcerptChars] + "…";

        return
            $"[turn summarizer failed: {error}]\n\n" +
            $"Truncated turn excerpt ({Math.Min(raw?.Length ?? 0, FallbackExcerptChars)} chars):\n{excerpt}";
    }
}

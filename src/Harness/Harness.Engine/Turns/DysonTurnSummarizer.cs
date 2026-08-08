using System.Text;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// One-shot Completions summarizer for turn context compression (SummarizeTurns).
/// Cap each summary at <see cref="MaxSummaryTokens"/> (tiktoken).
/// </summary>
public static class DysonTurnSummarizer
{
    public const int MaxSummaryTokens = 2_000;
    public const int FallbackExcerptChars = 1_500;

    /// <summary>True when a non-empty <see cref="DysonAgentTurn.ContextSummary"/> is set.</summary>
    public static bool HasSummary(DysonAgentTurn turn) =>
        turn is not null && !string.IsNullOrWhiteSpace(turn.ContextSummary);

    /// <summary>
    /// Builds compact turn text for the summarizer (instruction, assistant, tool log).
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

        return sb.ToString().Trim();
    }

    /// <summary>
    /// One-shot Completions summarize. Returns summary text, or a truncated excerpt on failure.
    /// </summary>
    public static async Task<string> SummarizeAsync(
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        DysonAgentTurn turn,
        string? reason = null,
        IDysonTokenCounter? tokens = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(turn);

        tokens ??= new DysonTiktokenTokenCounter();
        var raw = FormatTurnBody(turn);
        if (string.IsNullOrWhiteSpace(raw))
            return "(empty turn)";

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
                        ["content"] = DysonTurnSummarizerPrompt.System,
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
                return Fallback(raw, response.Error);

            var parsed = OpenAiCompletionsClient.Parse(response.Value);
            if (parsed.IsError)
                return Fallback(raw, parsed.Error);

            var summary = parsed.Value.Content?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(summary))
                return Fallback(raw, "Turn summarizer returned empty content.");

            return DysonWebSearchSummarizer.TrimToMaxTokens(summary, tokens, MaxSummaryTokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fallback(raw, ex.Message);
        }
    }

    /// <summary>Transcript stub: turnId header + summary body.</summary>
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

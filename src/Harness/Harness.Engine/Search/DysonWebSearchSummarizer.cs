using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Summarizes raw web search/fetch payloads inside the tool executor so HTML / large
/// extracts never enter the parent agent transcript (unless WebFetch fullHtml).
/// </summary>
public static class DysonWebSearchSummarizer
{
    public const int SkipBelowTokens = 1_500;
    public const int MaxSummaryTokens = 10_000;
    public const int FallbackExcerptChars = 2_000;

    private static readonly HashSet<string> ToolNames = new(StringComparer.Ordinal)
    {
        "FreeSearch",
        "FreeSearchAdvanced",
        "SearchWithSynthesis",
        "FreeExtract",
        "WebFetch",
        "FetchGithubReadme",
    };

    public static bool IsWebSearchTool(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && ToolNames.Contains(toolName);

    /// <summary>
    /// Always summarize WebFetch (caller must skip when fullHtml). For other tools,
    /// skip when already ≤ <see cref="SkipBelowTokens"/>.
    /// </summary>
    public static bool ShouldSummarize(string toolName, string content, IDysonTokenCounter tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (!IsWebSearchTool(toolName))
            return false;

        if (string.Equals(toolName, "WebFetch", StringComparison.Ordinal))
            return true;

        return tokens.CountTokens(content ?? "") > SkipBelowTokens;
    }

    /// <summary>
    /// One-shot Completions summarize. Returns summary text, or a truncated raw excerpt on failure.
    /// </summary>
    public static async Task<string> SummarizeAsync(
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        string toolName,
        string argumentsJson,
        string rawContent,
        string? summarizePrompt = null,
        IDysonTokenCounter? tokens = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);

        tokens ??= new DysonTiktokenTokenCounter();
        var raw = rawContent ?? "";

        try
        {
            var user = DysonWebSearchSummarizerPrompt.FormatUserMessage(
                toolName,
                argumentsJson,
                raw,
                summarizePrompt);

            // Soft trim of summarizer input (~32K tokens) beyond char cap in FormatUserMessage.
            user = TrimToMaxTokens(user, tokens, 32_000);

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
                        ["content"] = DysonWebSearchSummarizerPrompt.System,
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
                return Fallback(raw, "Summarizer returned empty content.");

            return TrimToMaxTokens(summary, tokens, MaxSummaryTokens);
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

    private static string Fallback(string raw, string error)
    {
        var excerpt = raw ?? "";
        if (excerpt.Length > FallbackExcerptChars)
            excerpt = excerpt[..FallbackExcerptChars] + "…";

        return
            $"[web summarizer failed: {error}]\n\n" +
            $"Truncated raw excerpt ({Math.Min(raw?.Length ?? 0, FallbackExcerptChars)} chars):\n{excerpt}";
    }

    /// <summary>Hard-cap text to ≤ <paramref name="maxTokens"/> via binary search on substring length.</summary>
    public static string TrimToMaxTokens(string text, IDysonTokenCounter tokens, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            return text ?? "";

        if (tokens.CountTokens(text) <= maxTokens)
            return text;

        const string suffix = "…";
        var suffixTokens = tokens.CountTokens(suffix);
        var budget = Math.Max(0, maxTokens - suffixTokens);

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (tokens.CountTokens(text[..mid]) <= budget)
                lo = mid;
            else
                hi = mid - 1;
        }

        if (lo <= 0)
            return suffixTokens <= maxTokens ? suffix : "";

        return text[..lo] + suffix;
    }
}

namespace DysonHarness;

/// <summary>
/// Editable constants for the web-search/fetch tool-result summarizer.
/// Summarizer system/user text never enters the main agent transcript.
/// </summary>
public static class DysonWebSearchSummarizerPrompt
{
    /// <summary>System instruction for the one-shot summarizer completion.</summary>
    public const string System = """
        You distill raw web search/fetch tool output for an AI coding agent.

        Goals:
        - Keep concrete facts that answer the agent’s likely need: names, dates, numbers, APIs, versions, commands, and source URLs.
        - When an “Agent focus” section is present, honor it: prioritize those facts and structure; still drop HTML, navigation, ads, cookie banners, and boilerplate.
        - Prefer short bullet facts plus a brief “Sources” list of URLs.
        - Be concise by default. When the source is dense with relevant detail, you may use up to about 2000 tokens of summary. Never exceed ~10000 tokens.
        - If the input is already compact JSON SERP results, compress to ranked bullets — do not re-emit the full JSON.
        - Do not invent facts. If the payload is empty or unusable, say so briefly.
        """;

    /// <summary>
    /// Builds the summarizer user message (tool name + args snippet + optional focus + raw body).
    /// Truncation for summarizer input only — never returned to the main agent.
    /// </summary>
    public static string FormatUserMessage(
        string toolName,
        string argumentsJson,
        string rawBody,
        string? summarizePrompt = null)
    {
        var args = argumentsJson ?? "{}";
        if (args.Length > 4_000)
            args = args[..4_000] + "…";

        var body = rawBody ?? "";
        if (body.Length > MaxInputChars)
            body = body[..MaxInputChars] + "\n…[truncated for summarizer input]";

        var focus = string.IsNullOrWhiteSpace(summarizePrompt)
            ? ""
            : $"""

                Agent focus:
                {summarizePrompt.Trim()}
                """;

        return $"""
            Tool: {toolName}
            Arguments:
            {args}
            {focus}
            Raw tool result:
            {body}
            """;
    }

    /// <summary>Max characters of raw body fed into the summarizer (not the agent).</summary>
    public const int MaxInputChars = 80_000;
}

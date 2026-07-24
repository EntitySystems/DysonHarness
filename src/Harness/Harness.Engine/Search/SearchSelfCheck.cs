namespace DysonHarness;

/// <summary>
/// Minimal self-check for SSRF URL validation + free-engine parsers (no test framework).
/// Call from demo host or a one-shot console path when verifying search tooling.
/// </summary>
public static class SearchSelfCheck
{
    public static VoidResult<string> RunSsrfChecks()
    {
        string[] mustBlock =
        [
            "http://localhost/admin",
            "http://127.0.0.1/",
            "http://[::1]/",
            "http://169.254.169.254/latest/meta-data/",
            "http://192.168.1.1/",
            "http://10.0.0.5/",
            "http://172.16.0.1/",
            "ftp://example.com/",
            "http://metadata.google.internal/",
        ];

        foreach (var url in mustBlock)
        {
            var result = SearchHttp.ValidateUrl(url);
            if (result.IsSuccess)
                return new VoidResult<string>($"SSRF check failed: expected block for {url}");
        }

        var ok = SearchHttp.ValidateUrl("https://example.com/path");
        if (ok.IsError)
            return new VoidResult<string>($"SSRF check failed: expected allow for https://example.com/path ({ok.Error})");

        // Primary free parser: DuckDuckGo HTML result__a (+ optional snippet)
        const string ddgSample = """
            <div class="result results_links">
            <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa&rut=x">Example DDG Title</a>
            <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa">Example snippet text here.</a>
            </div>
            """;
        var ddgParsed = SearchEngines.ParseDuckDuckGoHtml(ddgSample, 5);
        if (ddgParsed.Count < 1
            || ddgParsed[0].Url != "https://example.com/a"
            || ddgParsed[0].Title != "Example DDG Title")
        {
            return new VoidResult<string>("DuckDuckGo parser self-check failed.");
        }

        // Bing RSS fallback (HTML SERP captcha path removed)
        const string bingRssSample = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel>
            <item>
              <title>Example Bing Title</title>
              <link>https://example.com/b</link>
              <description><![CDATA[Example Bing snippet text.]]></description>
            </item>
            </channel></rss>
            """;
        var bingParsed = SearchEngines.ParseBingRss(bingRssSample, 5);
        if (bingParsed.Count < 1
            || bingParsed[0].Url != "https://example.com/b"
            || bingParsed[0].Title != "Example Bing Title")
        {
            return new VoidResult<string>("Bing RSS parser self-check failed.");
        }

        var summarizer = RunSummarizerPolicyChecks();
        if (summarizer.IsError)
            return summarizer;

        return VoidResult<string>.Success;
    }

    /// <summary>Policy checks for web-tool summarizer (no LLM call).</summary>
    public static VoidResult<string> RunSummarizerPolicyChecks()
    {
        var tokens = new DysonTiktokenTokenCounter();

        if (!DysonWebSearchSummarizer.IsWebSearchTool("WebFetch")
            || !DysonWebSearchSummarizer.IsWebSearchTool("FreeSearch")
            || DysonWebSearchSummarizer.IsWebSearchTool("ReadFile"))
        {
            return new VoidResult<string>("Summarizer tool-name set self-check failed.");
        }

        // WebFetch always summarizes (executor skips this path when fullHtml:true).
        if (!DysonWebSearchSummarizer.ShouldSummarize("WebFetch", "hi", tokens))
            return new VoidResult<string>("WebFetch should always summarize.");

        if (DysonWebSearchSummarizer.ShouldSummarize("FreeSearch", "short", tokens))
            return new VoidResult<string>("FreeSearch should skip when well under 1500 tokens.");

        var dense = new string('x', 20_000);
        if (!DysonWebSearchSummarizer.ShouldSummarize("FreeExtract", dense, tokens))
            return new VoidResult<string>("FreeExtract should summarize when over 1500 tokens.");

        var trimmed = DysonWebSearchSummarizer.TrimToMaxTokens(dense, tokens, 50);
        if (tokens.CountTokens(trimmed) > 50)
            return new VoidResult<string>("TrimToMaxTokens failed to enforce token cap.");

        var user = DysonWebSearchSummarizerPrompt.FormatUserMessage(
            "WebFetch",
            """{"url":"https://example.com"}""",
            "<html>ok</html>");
        if (!user.Contains("WebFetch", StringComparison.Ordinal)
            || !user.Contains("<html>ok</html>", StringComparison.Ordinal))
        {
            return new VoidResult<string>("Summarizer user-message format self-check failed.");
        }

        const string focus = "list Billboard Global 200 #1 song and artist with source URL";
        var focused = DysonWebSearchSummarizerPrompt.FormatUserMessage(
            "WebFetch",
            """{"url":"https://example.com"}""",
            "<html>ok</html>",
            focus);
        if (!focused.Contains("Agent focus:", StringComparison.Ordinal)
            || !focused.Contains(focus, StringComparison.Ordinal))
        {
            return new VoidResult<string>("Summarizer focus-prompt format self-check failed.");
        }

        if (!DysonWebSearchSummarizerPrompt.System.Contains("Agent focus", StringComparison.Ordinal))
            return new VoidResult<string>("Summarizer system prompt should mention Agent focus.");

        return VoidResult<string>.Success;
    }
}

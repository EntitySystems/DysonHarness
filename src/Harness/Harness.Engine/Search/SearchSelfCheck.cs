namespace DysonHarness;

/// <summary>
/// Minimal self-check for SSRF URL validation (no test framework).
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

        // Smoke: Bing HTML parser accepts a minimal synthetic SERP fragment
        const string sample = """
            <li class="b_algo"><h2><a href="https://example.com/a">Example Title</a></h2><p>Example snippet text here.</p></li>
            """;
        var parsed = SearchEngines.ParseBingHtml(sample, 5);
        if (parsed.Count != 1 || parsed[0].Url != "https://example.com/a")
            return new VoidResult<string>("Bing parser self-check failed.");

        return VoidResult<string>.Success;
    }
}

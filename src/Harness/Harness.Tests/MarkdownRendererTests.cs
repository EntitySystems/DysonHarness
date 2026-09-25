using System.Diagnostics;
using Harness.UI.Markdown;

namespace Harness.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void ToHtml_highlights_csharp_fences_without_colorcode_wrapper()
    {
        var html = MarkdownRenderer.ToHtml("```csharp\npublic class Foo {}\n```").Value;

        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
        Assert.True(
            html.Contains("class=\"keyword\"", StringComparison.Ordinal)
            || html.Contains("class=\"controlKeyword\"", StringComparison.Ordinal),
            html);
        Assert.DoesNotContain("<div class=\"csharp\">", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code class=\"language-csharp\">", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```go\npackage main\n```")]
    [InlineData("```\nplain fence\n```")]
    public void ToHtml_leaves_unknown_or_unlabeled_fences_as_escaped_code(string markdown)
    {
        var html = MarkdownRenderer.ToHtml(markdown).Value;

        Assert.Contains("<pre><code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"keyword\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"controlKeyword\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHtml_does_not_emit_live_script_from_markdown_html()
    {
        var html = MarkdownRenderer.ToHtml("hello <script>alert(1)</script>").Value;

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_escapes_html_inside_highlighted_fences()
    {
        var html = MarkdownRenderer.ToHtml("```csharp\n<img src=x onerror=alert(1)>\n```").Value;

        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_adds_noopener_noreferrer_on_http_links()
    {
        var html = MarkdownRenderer.ToHtml("[docs](https://example.com/x)").Value;

        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/x\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToHtml_returns_empty_markup_for_blank_input(string? markdown)
    {
        Assert.Equal("", MarkdownRenderer.ToHtml(markdown).Value);
    }

    [Fact]
    public void ToHtml_returns_same_value_instance_for_identical_source()
    {
        const string source = "hello **world**";
        var first = MarkdownRenderer.ToHtml(source);
        var second = MarkdownRenderer.ToHtml(source);
        var other = MarkdownRenderer.ToHtml("other markdown");

        Assert.Same(first.Value, second.Value);
        Assert.Contains("other markdown", other.Value, StringComparison.Ordinal);
        Assert.NotSame(first.Value, other.Value);
    }

    [Fact]
    public void ToHtml_eviction_on_overflow_is_transparent_to_callers()
    {
        const string earlyMarkdown = "eviction-probe-early markdown **entry**";
        var earlyHtml = MarkdownRenderer.ToHtml(earlyMarkdown).Value;

        // Push more than the 64-entry cache capacity through so the early entry is evicted.
        for (var i = 0; i < 80; i++)
            MarkdownRenderer.ToHtml($"eviction-probe-filler-{i} markdown *{i}*");

        var rerendered = MarkdownRenderer.ToHtml(earlyMarkdown).Value;

        Assert.Equal(earlyHtml, rerendered);
    }

    [Fact]
    public void ToHtml_highlights_json_fences()
    {
        var html = MarkdownRenderer.ToHtml("```json\n{ \"a\": 1 }\n```").Value;

        Assert.Contains("language-json", html, StringComparison.Ordinal);
        Assert.Contains("<span", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code class=\"language-json\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHtml_bounds_catastrophic_json_highlight_and_skips_a_longer_fence()
    {
        var first = Stopwatch.StartNew();
        var html = MarkdownRenderer.ToHtml(JsonFence(CatastrophicJson)).Value;
        first.Stop();

        Assert.True(
            first.Elapsed < TimeSpan.FromSeconds(3),
            $"catastrophic json fence took {first.Elapsed.TotalMilliseconds:0} ms");
        Assert.Contains("<pre><code", html, StringComparison.Ordinal);
        Assert.Contains("netsend_physical_pooled_provision_e2e", html, StringComparison.Ordinal);

        var csharp = MarkdownRenderer.ToHtml("```csharp\npublic class Foo {}\n```").Value;
        Assert.Contains("language-csharp", csharp, StringComparison.Ordinal);
        Assert.True(
            csharp.Contains("class=\"keyword\"", StringComparison.Ordinal)
            || csharp.Contains("class=\"controlKeyword\"", StringComparison.Ordinal),
            csharp);

        var second = Stopwatch.StartNew();
        var longer = MarkdownRenderer.ToHtml(JsonFence(CatastrophicJson + "\n\"extra\": true")).Value;
        second.Stop();

        Assert.True(
            second.Elapsed < TimeSpan.FromMilliseconds(500),
            $"extended fence took {second.Elapsed.TotalMilliseconds:0} ms");
        Assert.Contains("netsend_physical_pooled_provision_e2e", longer, StringComparison.Ordinal);
        Assert.Contains("extra", longer, StringComparison.Ordinal);
    }

    private static string JsonFence(string body) => $"```json\n{body}\n```";

    /// <summary>
    /// ColorCode's JSON string rule backtracks on this fence (escaped quotes inside a prepare array).
    /// </summary>
    private const string CatastrophicJson = """
        "netsend_physical_pooled_provision_e2e": {
          "display": "NetSend physical pooled provision",
          "description": "Manual only: live DigitalOcean + Cloudflare pooled provision (new VM, co-placement, reboot, idle stop/warm-up, purge + scale-in). Excluded from merge gates.",
          "prepare": ["dbmigrate --database postgres_dev", "python DevelopmentEnvironment/scripts/netsend_e2e_tunnel.py check (timeout 180)", "dotnet build CashTrack.E2E.Tests.csproj"],
          "argv": "dotnet run --no-build -- --flow \"NetSend physical pooled provision\"",
          "timeout_seconds": 7200,
          "managed_services": ["CashTrackServer", "CashTrackServerOfficeApi", "CashTrackServerOfficeWorker"],
          "restart_managed_services": true,
          "depends_on_env_profiles": ["cashtrack_server_dev"],
          "env": "same as paid set",
          "managed_service_env": {
            "CASHTRACK_STRIPE_POLL_INTERVAL_SECONDS": "15",
            "CASHTRACK_NETSEND_DESIRED_IMAGE_TAG": "local-build",
            "CASHTRACK_NETSEND_POOL_MIN_NODE_LIFETIME_MINUTES": "5"
          },
          "database_connection_env": {
            "CASHTRACK_E2E_OTP_DB_CONNECTION_STRING": {"database": "postgres_dev", "format": "dotnet"},
            "CASHTRACK_OFFICE_NETSEND_AZURESQL_STR": {"database": "office_netsend_sql_dev", "format": "dotnet"}
          }
        }
        """;
}

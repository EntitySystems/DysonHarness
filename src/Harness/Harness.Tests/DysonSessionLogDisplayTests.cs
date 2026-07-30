using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Session log rail classifier heuristics (Xunit).</summary>
public class DysonSessionLogDisplayTests
{
    [Fact]
    public void Parse_ClassifiesInfoWarnError()
    {
        AssertRow("turn complete: ok", DysonSessionLogLevel.Info, "info");
        AssertRow("usage cache: hit", DysonSessionLogLevel.Info, "info");
        AssertRow("prompt: hello", DysonSessionLogLevel.Info, "info");
        AssertRow("mode → work", DysonSessionLogLevel.Info, "info");

        AssertRow("soft-pause before next tool", DysonSessionLogLevel.Warn, "warn");
        AssertRow("retry after transient error", DysonSessionLogLevel.Warn, "warn");
        AssertRow("nudge: continue", DysonSessionLogLevel.Warn, "warn");
        AssertRow("fallback to default slug", DysonSessionLogLevel.Warn, "warn");

        AssertRow("OpenAI Files upload failed", DysonSessionLogLevel.Error, "error");
        AssertRow("GET /v1/files 404 not found", DysonSessionLogLevel.Error, "error");
        AssertRow("Unhandled exception in provider", DysonSessionLogLevel.Error, "error");
        AssertRow("fatal: provider unavailable", DysonSessionLogLevel.Error, "error");
    }

    [Fact]
    public void Parse_ErrorWinsOverWarn_AndTrimsBody()
    {
        var row = DysonSessionLogDisplay.Parse("  upload failed after retry  ");
        if (row.Level != DysonSessionLogLevel.Error || row.Badge != "error")
            throw new InvalidOperationException($"Expected error over warn, got {row.Level}/{row.Badge}.");
        if (row.Body != "upload failed after retry")
            throw new InvalidOperationException($"Body must be trimmed: '{row.Body}'.");
    }

    private static void AssertRow(string line, DysonSessionLogLevel level, string badge)
    {
        var row = DysonSessionLogDisplay.Parse(line);
        if (row.Level != level || row.Badge != badge || row.Body != line.Trim())
        {
            throw new InvalidOperationException(
                $"Parse('{line}'): expected {level}/{badge}, got {row.Level}/{row.Badge} body='{row.Body}'.");
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Tests;

/// <summary>
/// Fails on UTF-8 text that was decoded as Windows-1252 and saved again (mojibake such as <c>U+00E2 U+20AC U+201D</c>
/// for an em dash). WPF chrome strings are C# literals, so a mangled source file ships mangled glyphs.
/// </summary>
public class DysonSourceEncodingScanTests
{
    // Lead byte of a 2/3/4-byte UTF-8 sequence read as cp1252 (U+00C2 U+00C3 U+00E2 U+00F0), then a continuation
    // byte 0x80-0xBF read as cp1252 (U+0080-U+00BF or the cp1252 0x80-0x9F punctuation).
    private static readonly Regex Mojibake = new(
        "[\u00C2\u00C3\u00E2\u00F0][\u0080-\u00BF\u20AC\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039"
        + "\u0152\u017D\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u017E\u0178]",
        RegexOptions.CultureInvariant);

    private static readonly string[] Extensions = [".cs", ".razor", ".js", ".css", ".html", ".md", ".xaml", ".swift"];

    [Fact]
    public void Mojibake_pattern_catches_a_cp1252_round_trip()
    {
        // What the Dyson Browser chrome showed: back arrow, em dash, tab close, snip.
        foreach (var mangled in new[] { "\u00E2\u2020\u0090", "\u00E2\u20AC\u201D", "\u00C3\u2014", "\u00E2\u0153\u201A" })
            Assert.Matches(Mojibake, mangled);
        Assert.DoesNotMatch(Mojibake, "\u2190 \u2192 \u21BB \u2702 \u00D7 \u2014 \u00E2t\u00E9 na\u00EFve");
    }

    [Fact]
    public void Repository_text_files_have_no_mojibake()
    {
        var root = FindRepoRoot();
        var failures = new List<string>();
        foreach (var top in new[] { "src", "docs", "rules", "skills" })
        {
            var dir = Path.Combine(root, top);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    || file.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules"))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file, Encoding.UTF8);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (Mojibake.IsMatch(lines[i]))
                        failures.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Mojibake (UTF-8 read as cp1252):" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Windows_browser_chrome_declares_real_glyphs()
    {
        var text = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Harness", "Harness.WindowsBrowser", "DysonWindowsBrowserWindow.cs"),
            Encoding.UTF8);
        // Back, forward, reload, snip, tab close, title separator.
        foreach (var glyph in new[] { "\"\u2190\"", "\"\u2192\"", "\"\u21BB\"", "\"\u2702\"", "\"\u00D7\"", " \u2014 Dyson Browser" })
            Assert.Contains(glyph, text, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Harness", "Harness.WindowsBrowser")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}

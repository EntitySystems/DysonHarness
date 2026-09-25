using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ColorCode;
using ColorCode.Compilation;
using ColorCode.Styling;

namespace Harness.UI.Markdown;

internal static class ColorCodeHtml
{
    internal const int MaxHighlightedChars = 64 * 1024;

    private static readonly TimeSpan HighlightTimeout = TimeSpan.FromSeconds(1);

    // ponytail: StyleDictionary.DefaultDark allocates on every get; HtmlClassFormatter.Writer is
    // instance state, so one formatter + lock (upgrade: per-call formatter if chat volume needs it).
    private static readonly StyleDictionary DarkStyles = StyleDictionary.DefaultDark;
    private static readonly HtmlClassFormatter Formatter = new(DarkStyles);
    private static readonly object FormatterGate = new();
    private static readonly HashSet<string> TimeoutInstalled = new(StringComparer.Ordinal);

    // ponytail: exact + prefix failure memory. A streaming fence grows by append, so one timeout
    // suppresses the rest of that fence. Ceiling: a different fence that starts with a remembered
    // failure stays plain. Upgrade: key failures by fence identity.
    private static readonly List<string> HighlightFailures = [];

    internal static string? TryFormat(string source, ILanguage language)
    {
        if (source.Length > MaxHighlightedChars)
            return null;

        lock (FormatterGate)
        {
            if (IsKnownHighlightFailure(source))
                return null;

            try
            {
                // ColorCode 2.0.15 compiles language regexes with an infinite match timeout.
                // Missing private fields fail closed: do not match unbounded on the circuit.
                if (!TryEnsureMatchTimeout(language))
                    return null;

                return Formatter.GetHtmlString(source, language);
            }
            catch (Exception ex) when (ex is RegexMatchTimeoutException
                                       || ex.InnerException is RegexMatchTimeoutException)
            {
                RememberHighlightFailure(source);
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static bool TryEnsureMatchTimeout(ILanguage language)
    {
        if (TimeoutInstalled.Contains(language.Id))
            return true;

        var parser = typeof(HtmlClassFormatter).GetField(
            "languageParser",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Formatter);
        var compiler = parser?.GetType().GetField(
            "languageCompiler",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(parser);
        if (compiler is not ILanguageCompiler languageCompiler)
            return false;

        var compiled = languageCompiler.Compile(language);
        var regex = compiled.Regex;
        if (regex.MatchTimeout == HighlightTimeout)
        {
            TimeoutInstalled.Add(language.Id);
            return true;
        }

        if (regex.MatchTimeout != Regex.InfiniteMatchTimeout)
            return false;

        compiled.Regex = new Regex(regex.ToString(), regex.Options, HighlightTimeout);
        if (compiled.Regex.MatchTimeout != HighlightTimeout)
            return false;

        TimeoutInstalled.Add(language.Id);
        return true;
    }

    private static bool IsKnownHighlightFailure(string source)
    {
        foreach (var failed in HighlightFailures)
        {
            if (source.StartsWith(failed, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void RememberHighlightFailure(string source)
    {
        if (source.Length == 0 || IsKnownHighlightFailure(source))
            return;

        HighlightFailures.Add(source);
    }

    /// <summary>ColorCode 2.0.15 envelope: &lt;div class="…"&gt;&lt;pre&gt;…&lt;/pre&gt;&lt;/div&gt;.</summary>
    internal static bool TryUnwrap(string html, out string inner)
    {
        inner = "";
        if (string.IsNullOrEmpty(html))
            return false;

        var working = html.AsSpan().TrimEnd("\r\n");
        const string prefix = "<div class=\"";
        if (!working.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var classEnd = working[prefix.Length..].IndexOf("\"><pre>", StringComparison.Ordinal);
        if (classEnd < 0)
            return false;

        classEnd += prefix.Length;
        if (!IsCssClassName(working[prefix.Length..classEnd]))
            return false;

        var start = classEnd + "\"><pre>".Length;
        if (start < working.Length && working[start] == '\r')
            start++;
        if (start < working.Length && working[start] == '\n')
            start++;

        const string footer = "</pre></div>";
        if (!working.EndsWith(footer, StringComparison.Ordinal))
            return false;

        var end = working.Length - footer.Length;
        if (end > start && working[end - 1] == '\n')
            end--;
        if (end > start && working[end - 1] == '\r')
            end--;

        if (start > end)
            return false;

        inner = working[start..end].ToString();
        return true;
    }

    internal static bool TrySplitToSourceLines(string innerHtml, int expectedLineCount, out string[] lineHtml)
    {
        lineHtml = [];
        if (expectedLineCount < 1 || innerHtml is null)
            return false;

        var normalized = innerHtml.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = new List<string>(expectedLineCount);
        var openSpans = new List<string>();
        var currentLine = new StringBuilder();

        for (var index = 0; index < normalized.Length;)
        {
            var character = normalized[index];
            if (character == '\n')
            {
                AppendClosingSpans(currentLine, openSpans);
                lines.Add(currentLine.ToString());
                currentLine.Clear();
                AppendOpenSpans(currentLine, openSpans);
                index++;
                continue;
            }

            if (character != '<')
            {
                if (character == '>')
                    return false;

                currentLine.Append(character);
                index++;
                continue;
            }

            if (normalized.AsSpan(index).StartsWith("</span>", StringComparison.Ordinal))
            {
                if (openSpans.Count == 0)
                    return false;

                currentLine.Append("</span>");
                openSpans.RemoveAt(openSpans.Count - 1);
                index += "</span>".Length;
                continue;
            }

            const string spanPrefix = "<span class=\"";
            if (!normalized.AsSpan(index).StartsWith(spanPrefix, StringComparison.Ordinal))
                return false;

            var classStart = index + spanPrefix.Length;
            var closeOffset = normalized.AsSpan(classStart).IndexOf("\">", StringComparison.Ordinal);
            if (closeOffset < 0)
                return false;

            var classEnd = classStart + closeOffset;
            var span = normalized[index..(classEnd + "\">".Length)];
            if (!IsCssClassName(normalized.AsSpan(classStart, classEnd - classStart)))
                return false;

            currentLine.Append(span);
            openSpans.Add(span);
            index += span.Length;
        }

        if (openSpans.Count != 0)
            return false;

        lines.Add(currentLine.ToString());
        if (lines.Count != expectedLineCount)
            return false;

        lineHtml = [.. lines];
        return true;
    }

    internal static string[]? TryHighlightSourceLines(string? relativePath, string content)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || content.Length > MaxHighlightedChars)
            return null;

        string extension;
        try
        {
            extension = Path.GetExtension(relativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var language = ColorCodeLanguages.TryResolve(extension);
        if (language is null)
            return null;

        var formatted = TryFormat(content, language);
        if (formatted is null || !TryUnwrap(formatted, out var inner))
            return null;

        var expectedLineCount = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
        return TrySplitToSourceLines(inner, expectedLineCount, out var lines) ? lines : null;
    }

    private static bool IsCssClassName(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !char.IsLetter(value[0]))
            return false;

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not '-' and not '_')
                return false;
        }

        return true;
    }

    private static void AppendClosingSpans(StringBuilder builder, List<string> openSpans)
    {
        for (var index = openSpans.Count - 1; index >= 0; index--)
            builder.Append("</span>");
    }

    private static void AppendOpenSpans(StringBuilder builder, List<string> openSpans)
    {
        foreach (var span in openSpans)
            builder.Append(span);
    }
}

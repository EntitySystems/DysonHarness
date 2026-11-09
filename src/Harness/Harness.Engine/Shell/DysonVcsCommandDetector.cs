using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Pure check: does a ShellExecute command invoke <c>gh</c> or <c>git</c>?
/// Command-line shells are split into segments; Python/Node commands are code and use a literal scan.
/// </summary>
internal static partial class DysonVcsCommandDetector
{
    /// <summary>
    /// ponytail: code-shell scan is a string-literal regex, so a stray "git" literal costs one extra
    /// free 304. Upgrade path: a real Python/JS parser.
    /// </summary>
    [GeneratedRegex("""["'](?:[^"'\r\n]*[\\/])?(?:gh|git)(?:\.exe)?(?:["']|\s)""", RegexOptions.IgnoreCase)]
    private static partial Regex CodeShellLiteral();

    internal static bool InvokesVcs(string shellName, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (IsCodeShell(shellName))
            return CodeShellLiteral().IsMatch(command);

        foreach (var segment in SplitSegments(command))
        {
            if (SegmentInvokesVcs(segment))
                return true;
        }

        return false;
    }

    private static bool IsCodeShell(string shellName) =>
        shellName.Equals("Python", StringComparison.OrdinalIgnoreCase)
        || shellName.Equals("Node", StringComparison.OrdinalIgnoreCase);

    private static bool SegmentInvokesVcs(string segment)
    {
        var i = 0;
        while (true)
        {
            i = SkipSeparators(segment, i);
            if (i >= segment.Length)
                return false;

            if (TryStripPrefix(segment, ref i))
                continue;

            var token = ReadToken(segment, i);
            if (token.Length == 0)
                return false;

            if (IsEnvAssignment(token))
            {
                i += token.Length;
                continue;
            }

            return IsVcsFileName(token);
        }
    }

    private static int SkipSeparators(string text, int i)
    {
        while (i < text.Length && IsSeparator(text[i]))
            i++;
        return i;
    }

    private static bool IsSeparator(char c) => c is ' ' or '\t' or '\r' or '\n';

    /// <summary>Strip one leading <c>(</c>, <c>{</c>, <c>$(</c>, <c>!</c>, or a PowerShell <c>&amp; </c> / <c>. </c> call operator.</summary>
    private static bool TryStripPrefix(string text, ref int i)
    {
        if (i + 1 < text.Length && text[i] == '$' && text[i + 1] == '(')
        {
            i += 2;
            return true;
        }

        var c = text[i];
        if (c is '(' or '{' or '!')
        {
            i++;
            return true;
        }

        if (c is '&' or '.'
            && i + 1 < text.Length
            && IsSeparator(text[i + 1]))
        {
            i++;
            return true;
        }

        return false;
    }

    private static string ReadToken(string text, int start)
    {
        var quote = text[start] is '"' or '\'' ? text[start] : '\0';
        var i = quote == '\0' ? start : start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                    return text[start..(i + 1)];
            }
            else if (IsSeparator(c) || c is '&' or '|' or ';' or '(' or ')')
            {
                break;
            }

            i++;
        }

        return text[start..i];
    }

    private static bool IsEnvAssignment(string token)
    {
        if (token.Length < 2 || token[0] is '"' or '\'' or '=' || !IsNameChar(token[0], first: true))
            return false;

        for (var i = 1; i < token.Length; i++)
        {
            if (token[i] == '=')
                return true;
            if (!IsNameChar(token[i], first: false))
                return false;
        }

        return false;
    }

    private static bool IsNameChar(char c, bool first) =>
        c is '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
        || (!first && c is >= '0' and <= '9');

    private static bool IsVcsFileName(string token)
    {
        if (token.Length >= 2 && token[0] == token[^1] && token[0] is '"' or '\'')
            token = token[1..^1];

        var slash = token.LastIndexOfAny(['/', '\\']);
        var name = (slash < 0 ? token : token[(slash + 1)..]).ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal))
            name = name[..^4];

        return name is "gh" or "git";
    }

    /// <summary>Split on newlines, <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, and a lone <c>&amp;</c>, outside quotes.</summary>
    private static IEnumerable<string> SplitSegments(string command)
    {
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            var cut = c is '\r' or '\n' or ';' or '|';
            if (!cut && c == '&' && (i + 1 >= command.Length || command[i + 1] != '&'))
                cut = true;
            if (!cut)
                continue;

            yield return command[start..i];
            if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
                i++;
            else if (c == '|' && i + 1 < command.Length && command[i + 1] == '|')
                i++;
            else if (c == '\r' && i + 1 < command.Length && command[i + 1] == '\n')
                i++;

            start = i + 1;
        }

        yield return command[start..];
    }
}

using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// OpenCode-style cascading text replacement for WriteFile targeted edits.
/// Port of anomalyco/opencode packages/opencode/src/tool/edit.ts replace() + replacers.
/// </summary>
public static class DysonTextEditApplier
{
    // Similarity thresholds for block-anchor fallback matching (OpenCode).
    private const double SingleCandidateSimilarityThreshold = 0.0;
    private const double MultipleCandidatesSimilarityThreshold = 0.3;

    public enum FailureKind
    {
        Identical,
        NotFound,
        Ambiguous,
    }

    public sealed class Failure
    {
        public required FailureKind Kind { get; init; }
        public required string Message { get; init; }
        public int MatchCount { get; init; }
    }

    public sealed class Success
    {
        public required string Content { get; init; }
        public int ReplacementCount { get; init; }
    }

    /// <summary>
    /// Replace <paramref name="oldText"/> with <paramref name="newText"/> using cascading matchers.
    /// Preserves file EOL; normalizes candidate old/new to that EOL before matching.
    /// </summary>
    public static Result<Success, Failure> TryReplace(
        string content,
        string oldText,
        string newText,
        bool replaceAll = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        if (oldText == newText)
        {
            return Result<Success, Failure>.AsError(new Failure
            {
                Kind = FailureKind.Identical,
                Message = "No changes to apply: old_text and new_text are identical.",
            });
        }

        var ending = DetectLineEnding(content);
        var oldNormalized = ConvertToLineEnding(NormalizeLineEndings(oldText), ending);
        var newNormalized = ConvertToLineEnding(NormalizeLineEndings(newText), ending);

        var notFound = true;
        var ambiguousCount = 0;

        foreach (var search in CascadeMatches(content, oldNormalized))
        {
            var index = content.IndexOf(search, StringComparison.Ordinal);
            if (index < 0)
                continue;

            notFound = false;

            if (replaceAll)
            {
                var count = 0;
                var replaced = ReplaceAllOrdinal(content, search, newNormalized, out count);
                return Result<Success, Failure>.AsValue(new Success
                {
                    Content = replaced,
                    ReplacementCount = count,
                });
            }

            var lastIndex = content.LastIndexOf(search, StringComparison.Ordinal);
            if (index != lastIndex)
            {
                ambiguousCount = CountOccurrences(content, search);
                continue;
            }

            var next = string.Concat(
                content.AsSpan(0, index),
                newNormalized,
                content.AsSpan(index + search.Length));
            return Result<Success, Failure>.AsValue(new Success
            {
                Content = next,
                ReplacementCount = 1,
            });
        }

        if (notFound)
        {
            return Result<Success, Failure>.AsError(new Failure
            {
                Kind = FailureKind.NotFound,
                Message =
                    "old_text not found. Provide a unique span from ReadFile (content after the '|' only — never include line-number prefixes like '123|'). " +
                    "Whitespace/indent/EOL differences are tolerated when the match is unique.",
                MatchCount = 0,
            });
        }

        return Result<Success, Failure>.AsError(new Failure
        {
            Kind = FailureKind.Ambiguous,
            Message =
                $"old_text matched {Math.Max(ambiguousCount, 2)} times; pass replace_all or more surrounding context to make the match unique.",
            MatchCount = Math.Max(ambiguousCount, 2),
        });
    }

    internal static string DetectLineEnding(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    internal static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    internal static string ConvertToLineEnding(string text, string ending) =>
        ending == "\n" ? text : text.Replace("\n", "\r\n", StringComparison.Ordinal);

    private static IEnumerable<string> CascadeMatches(string content, string find)
    {
        foreach (var m in SimpleReplacer(content, find))
            yield return m;
        foreach (var m in LineTrimmedReplacer(content, find))
            yield return m;
        foreach (var m in BlockAnchorReplacer(content, find))
            yield return m;
        foreach (var m in WhitespaceNormalizedReplacer(content, find))
            yield return m;
        foreach (var m in IndentationFlexibleReplacer(content, find))
            yield return m;
        foreach (var m in EscapeNormalizedReplacer(content, find))
            yield return m;
        foreach (var m in TrimmedBoundaryReplacer(content, find))
            yield return m;
        foreach (var m in ContextAwareReplacer(content, find))
            yield return m;
        foreach (var m in MultiOccurrenceReplacer(content, find))
            yield return m;
    }

    private static IEnumerable<string> SimpleReplacer(string _content, string find)
    {
        yield return find;
    }

    private static IEnumerable<string> LineTrimmedReplacer(string content, string find)
    {
        var originalLines = content.Split('\n');
        var searchLines = find.Split('\n');

        if (searchLines.Length > 0 && searchLines[^1].Length == 0)
            searchLines = searchLines[..^1];

        if (searchLines.Length == 0)
            yield break;

        for (var i = 0; i <= originalLines.Length - searchLines.Length; i++)
        {
            var matches = true;
            for (var j = 0; j < searchLines.Length; j++)
            {
                if (originalLines[i + j].Trim() != searchLines[j].Trim())
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
                continue;

            var matchStartIndex = 0;
            for (var k = 0; k < i; k++)
                matchStartIndex += originalLines[k].Length + 1;

            var matchEndIndex = matchStartIndex;
            for (var k = 0; k < searchLines.Length; k++)
            {
                matchEndIndex += originalLines[i + k].Length;
                if (k < searchLines.Length - 1)
                    matchEndIndex += 1;
            }

            yield return content[matchStartIndex..matchEndIndex];
        }
    }

    private static IEnumerable<string> BlockAnchorReplacer(string content, string find)
    {
        var originalLines = content.Split('\n');
        var searchLines = find.Split('\n').ToList();

        if (searchLines.Count < 3)
            yield break;

        if (searchLines[^1].Length == 0)
            searchLines.RemoveAt(searchLines.Count - 1);

        if (searchLines.Count < 3)
            yield break;

        var firstLineSearch = searchLines[0].Trim();
        var lastLineSearch = searchLines[^1].Trim();
        var searchBlockSize = searchLines.Count;

        var candidates = new List<(int StartLine, int EndLine)>();
        for (var i = 0; i < originalLines.Length; i++)
        {
            if (originalLines[i].Trim() != firstLineSearch)
                continue;

            for (var j = i + 2; j < originalLines.Length; j++)
            {
                if (originalLines[j].Trim() == lastLineSearch)
                {
                    candidates.Add((i, j));
                    break;
                }
            }
        }

        if (candidates.Count == 0)
            yield break;

        if (candidates.Count == 1)
        {
            var (startLine, endLine) = candidates[0];
            var actualBlockSize = endLine - startLine + 1;
            var similarity = 0.0;
            var linesToCheck = Math.Min(searchBlockSize - 2, actualBlockSize - 2);

            if (linesToCheck > 0)
            {
                for (var j = 1; j < searchBlockSize - 1 && j < actualBlockSize - 1; j++)
                {
                    var originalLine = originalLines[startLine + j].Trim();
                    var searchLine = searchLines[j].Trim();
                    var maxLen = Math.Max(originalLine.Length, searchLine.Length);
                    if (maxLen == 0)
                        continue;
                    var distance = Levenshtein(originalLine, searchLine);
                    similarity += (1.0 - (double)distance / maxLen) / linesToCheck;
                    if (similarity >= SingleCandidateSimilarityThreshold)
                        break;
                }
            }
            else
            {
                similarity = 1.0;
            }

            if (similarity >= SingleCandidateSimilarityThreshold)
                yield return SliceLines(content, originalLines, startLine, endLine);

            yield break;
        }

        (int StartLine, int EndLine)? bestMatch = null;
        var maxSimilarity = -1.0;

        foreach (var (startLine, endLine) in candidates)
        {
            var actualBlockSize = endLine - startLine + 1;
            var similarity = 0.0;
            var linesToCheck = Math.Min(searchBlockSize - 2, actualBlockSize - 2);

            if (linesToCheck > 0)
            {
                for (var j = 1; j < searchBlockSize - 1 && j < actualBlockSize - 1; j++)
                {
                    var originalLine = originalLines[startLine + j].Trim();
                    var searchLine = searchLines[j].Trim();
                    var maxLen = Math.Max(originalLine.Length, searchLine.Length);
                    if (maxLen == 0)
                        continue;
                    var distance = Levenshtein(originalLine, searchLine);
                    similarity += 1.0 - (double)distance / maxLen;
                }

                similarity /= linesToCheck;
            }
            else
            {
                similarity = 1.0;
            }

            if (similarity > maxSimilarity)
            {
                maxSimilarity = similarity;
                bestMatch = (startLine, endLine);
            }
        }

        if (maxSimilarity >= MultipleCandidatesSimilarityThreshold && bestMatch is { } best)
            yield return SliceLines(content, originalLines, best.StartLine, best.EndLine);
    }

    private static IEnumerable<string> WhitespaceNormalizedReplacer(string content, string find)
    {
        static string NormalizeWhitespace(string text) =>
            Regex.Replace(text, @"\s+", " ").Trim();

        var normalizedFind = NormalizeWhitespace(find);
        var lines = content.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (NormalizeWhitespace(line) == normalizedFind)
            {
                yield return line;
            }
            else
            {
                var normalizedLine = NormalizeWhitespace(line);
                if (!normalizedLine.Contains(normalizedFind, StringComparison.Ordinal))
                    continue;

                var words = find.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                    continue;

                var pattern = string.Join(
                    @"\s+",
                    words.Select(static w => Regex.Escape(w)));
                string? matched = null;
                try
                {
                    var match = Regex.Match(line, pattern);
                    if (match.Success)
                        matched = match.Value;
                }
                catch (RegexParseException)
                {
                    // Invalid pattern — skip.
                }

                if (matched is not null)
                    yield return matched;
            }
        }

        var findLines = find.Split('\n');
        if (findLines.Length <= 1)
            yield break;

        for (var i = 0; i <= lines.Length - findLines.Length; i++)
        {
            var block = string.Join('\n', lines.AsSpan(i, findLines.Length).ToArray());
            if (NormalizeWhitespace(block) == normalizedFind)
                yield return block;
        }
    }

    private static IEnumerable<string> IndentationFlexibleReplacer(string content, string find)
    {
        static string RemoveIndentation(string text)
        {
            var lines = text.Split('\n');
            var nonEmpty = lines.Where(static l => l.Trim().Length > 0).ToArray();
            if (nonEmpty.Length == 0)
                return text;

            var minIndent = nonEmpty.Min(static line =>
            {
                var n = 0;
                while (n < line.Length && char.IsWhiteSpace(line[n]))
                    n++;
                return n;
            });

            return string.Join('\n', lines.Select(line =>
                line.Trim().Length == 0 ? line : line[Math.Min(minIndent, line.Length)..]));
        }

        var normalizedFind = RemoveIndentation(find);
        var contentLines = content.Split('\n');
        var findLines = find.Split('\n');

        for (var i = 0; i <= contentLines.Length - findLines.Length; i++)
        {
            var block = string.Join('\n', contentLines.AsSpan(i, findLines.Length).ToArray());
            if (RemoveIndentation(block) == normalizedFind)
                yield return block;
        }
    }

    private static IEnumerable<string> EscapeNormalizedReplacer(string content, string find)
    {
        static string UnescapeString(string str) =>
            Regex.Replace(str, @"\\(n|t|r|'|""|`|\\|\n|\$)", static m =>
            {
                var c = m.Groups[1].Value;
                return c switch
                {
                    "n" => "\n",
                    "t" => "\t",
                    "r" => "\r",
                    "'" => "'",
                    "\"" => "\"",
                    "`" => "`",
                    "\\" => "\\",
                    "\n" => "\n",
                    "$" => "$",
                    _ => m.Value,
                };
            });

        var unescapedFind = UnescapeString(find);

        if (content.Contains(unescapedFind, StringComparison.Ordinal))
            yield return unescapedFind;

        var lines = content.Split('\n');
        var findLines = unescapedFind.Split('\n');

        for (var i = 0; i <= lines.Length - findLines.Length; i++)
        {
            var block = string.Join('\n', lines.AsSpan(i, findLines.Length).ToArray());
            if (UnescapeString(block) == unescapedFind)
                yield return block;
        }
    }

    private static IEnumerable<string> MultiOccurrenceReplacer(string content, string find)
    {
        var startIndex = 0;
        while (true)
        {
            var index = content.IndexOf(find, startIndex, StringComparison.Ordinal);
            if (index < 0)
                yield break;

            yield return find;
            startIndex = index + find.Length;
        }
    }

    private static IEnumerable<string> TrimmedBoundaryReplacer(string content, string find)
    {
        var trimmedFind = find.Trim();
        if (trimmedFind == find)
            yield break;

        if (content.Contains(trimmedFind, StringComparison.Ordinal))
            yield return trimmedFind;

        var lines = content.Split('\n');
        var findLines = find.Split('\n');

        for (var i = 0; i <= lines.Length - findLines.Length; i++)
        {
            var block = string.Join('\n', lines.AsSpan(i, findLines.Length).ToArray());
            if (block.Trim() == trimmedFind)
                yield return block;
        }
    }

    private static IEnumerable<string> ContextAwareReplacer(string content, string find)
    {
        var findLines = find.Split('\n').ToList();
        if (findLines.Count < 3)
            yield break;

        if (findLines[^1].Length == 0)
            findLines.RemoveAt(findLines.Count - 1);

        if (findLines.Count < 3)
            yield break;

        var contentLines = content.Split('\n');
        var firstLine = findLines[0].Trim();
        var lastLine = findLines[^1].Trim();

        for (var i = 0; i < contentLines.Length; i++)
        {
            if (contentLines[i].Trim() != firstLine)
                continue;

            for (var j = i + 2; j < contentLines.Length; j++)
            {
                if (contentLines[j].Trim() != lastLine)
                    continue;

                var blockLines = contentLines.AsSpan(i, j - i + 1).ToArray();
                var block = string.Join('\n', blockLines);

                if (blockLines.Length == findLines.Count)
                {
                    var matchingLines = 0;
                    var totalNonEmptyLines = 0;

                    for (var k = 1; k < blockLines.Length - 1; k++)
                    {
                        var blockLine = blockLines[k].Trim();
                        var findLine = findLines[k].Trim();
                        if (blockLine.Length > 0 || findLine.Length > 0)
                        {
                            totalNonEmptyLines++;
                            if (blockLine == findLine)
                                matchingLines++;
                        }
                    }

                    if (totalNonEmptyLines == 0 || (double)matchingLines / totalNonEmptyLines >= 0.5)
                        yield return block;
                }

                break;
            }
        }
    }

    private static string SliceLines(string content, string[] originalLines, int startLine, int endLine)
    {
        var matchStartIndex = 0;
        for (var k = 0; k < startLine; k++)
            matchStartIndex += originalLines[k].Length + 1;

        var matchEndIndex = matchStartIndex;
        for (var k = startLine; k <= endLine; k++)
        {
            matchEndIndex += originalLines[k].Length;
            if (k < endLine)
                matchEndIndex += 1;
        }

        return content[matchStartIndex..matchEndIndex];
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return Math.Max(a.Length, b.Length);

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static string ReplaceAllOrdinal(string content, string search, string replacement, out int count)
    {
        count = 0;
        if (search.Length == 0)
            return content;

        var sb = new System.Text.StringBuilder(content.Length);
        var start = 0;
        while (true)
        {
            var idx = content.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(content, start, content.Length - start);
                break;
            }

            sb.Append(content, start, idx - start);
            sb.Append(replacement);
            count++;
            start = idx + search.Length;
        }

        return sb.ToString();
    }

    private static int CountOccurrences(string content, string search)
    {
        if (search.Length == 0)
            return 0;

        var count = 0;
        var start = 0;
        while (true)
        {
            var idx = content.IndexOf(search, start, StringComparison.Ordinal);
            if (idx < 0)
                return count;
            count++;
            start = idx + search.Length;
        }
    }
}

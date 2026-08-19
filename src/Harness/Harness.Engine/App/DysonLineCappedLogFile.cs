using System.Text;

namespace DysonHarness;

/// <summary>
/// Thread-safe UTF-8 log file that keeps the newest <see cref="MaxLines"/> physical lines.
/// Physical lines are <c>\n</c> segments: a trailing newline is one line, not an extra empty one.
/// <c>\r\n</c> is still one physical line (<c>\r</c> stays on the previous segment).
/// </summary>
public sealed class DysonLineCappedLogFile
{
    public const int MaxLines = 5000;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    // ponytail: process-wide lock + O(n) rewrite at cap; upgrade to rolling segments if Error volume grows.
    private readonly object _gate = new();
    private bool _counted;
    private int _lineCount;
    private bool _endsWithNewline = true;

    public DysonLineCappedLogFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>
    /// Appends <paramref name="text"/> and drops the oldest physical lines when the file exceeds
    /// <see cref="MaxLines"/>. Opens with <see cref="FileShare.Read"/> so the file can be tailed.
    /// Never throws; I/O failures are swallowed.
    /// </summary>
    public void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            try
            {
                EnsureParentDirectory();
                if (!_counted)
                {
                    (_lineCount, _endsWithNewline) = CountPhysicalLinesOnDisk();
                    if (_lineCount > MaxLines)
                        TrimToNewest();
                    _counted = true;
                }

                if (text.IsEmpty)
                    return;

                var previousEndedWithNewline = _endsWithNewline;
                WriteAppend(text);
                _lineCount += CountAddedPhysicalLines(text, previousEndedWithNewline);
                _endsWithNewline = text[^1] == '\n';
                if (_lineCount > MaxLines)
                    TrimToNewest();
            }
            catch
            {
                _counted = false;
            }
        }
    }

    private void EnsureParentDirectory()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private void WriteAppend(ReadOnlySpan<char> text)
    {
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(text);
    }

    private (int Count, bool EndsWithNewline) CountPhysicalLinesOnDisk()
    {
        if (!File.Exists(_path))
            return (0, true);

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
            return (0, true);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var count = 0;
        var endsWithNewline = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    count++;
                    endsWithNewline = true;
                }
                else
                {
                    endsWithNewline = false;
                }
            }
        }

        if (!endsWithNewline)
            count++;

        return (count, endsWithNewline);
    }

    private static int CountAddedPhysicalLines(ReadOnlySpan<char> text, bool previousEndedWithNewline)
    {
        if (text.IsEmpty)
            return 0;

        var newlines = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                newlines++;
        }

        var added = newlines;
        if (text[^1] != '\n')
            added++;
        if (!previousEndedWithNewline)
            added--;

        return added < 0 ? 0 : added;
    }

    private void TrimToNewest()
    {
        if (!File.Exists(_path))
        {
            _lineCount = 0;
            _endsWithNewline = true;
            return;
        }

        string text;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();

        var lines = SplitPhysicalLines(text);
        if (lines.Count <= MaxLines)
        {
            _lineCount = lines.Count;
            _endsWithNewline = text.Length == 0 || text[^1] == '\n';
            return;
        }

        var kept = lines.GetRange(lines.Count - MaxLines, MaxLines);
        var originalEndsWithNewline = text[^1] == '\n';
        var rewritten = string.Join('\n', kept);
        if (originalEndsWithNewline)
            rewritten += "\n";

        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
            writer.Write(rewritten);

        _lineCount = MaxLines;
        _endsWithNewline = originalEndsWithNewline;
    }

    /// <summary>
    /// Splits on <c>\n</c>. A trailing newline does not add an extra empty line.
    /// </summary>
    private static List<string> SplitPhysicalLines(string text)
    {
        if (text.Length == 0)
            return [];

        var parts = text.Split('\n');
        if (parts[^1].Length == 0)
            return [.. parts[..^1]];

        return [.. parts];
    }
}

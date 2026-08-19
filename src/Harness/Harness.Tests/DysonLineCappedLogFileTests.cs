using System.Text;
using DysonHarness;

namespace Harness.Tests;

public class DysonLineCappedLogFileTests
{
    [Fact]
    public void Under_cap_lines_are_unchanged()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            var log = new DysonLineCappedLogFile(path);
            var expected = Lines(100, "line-");
            log.Append(expected);

            var content = File.ReadAllText(path);
            Assert.Equal(expected, content);
            Assert.Equal(100, CountPhysicalLines(content));
            Assert.Equal("line-0", PhysicalLines(content)[0]);
            Assert.Equal("line-99", PhysicalLines(content)[^1]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Exactly_cap_lines_are_unchanged()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            var log = new DysonLineCappedLogFile(path);
            var expected = Lines(DysonLineCappedLogFile.MaxLines, "line-");
            log.Append(expected);

            var content = File.ReadAllText(path);
            Assert.Equal(expected, content);
            Assert.Equal(DysonLineCappedLogFile.MaxLines, CountPhysicalLines(content));
            Assert.Equal("line-0", PhysicalLines(content)[0]);
            Assert.Equal($"line-{DysonLineCappedLogFile.MaxLines - 1}", PhysicalLines(content)[^1]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Over_cap_drops_oldest_and_keeps_newest()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            var log = new DysonLineCappedLogFile(path);
            log.Append(Lines(DysonLineCappedLogFile.MaxLines, "line-"));
            log.Append("newest\n");

            var content = File.ReadAllText(path);
            var lines = PhysicalLines(content);
            Assert.Equal(DysonLineCappedLogFile.MaxLines, lines.Length);
            Assert.Equal("line-1", lines[0]);
            Assert.Equal("newest", lines[^1]);
            Assert.DoesNotContain("line-0\n", content);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Multi_line_exception_text_counts_as_multiple_physical_lines()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            var log = new DysonLineCappedLogFile(path);
            log.Append("header\n");
            var exceptionText =
                "System.InvalidOperationException: boom\n" +
                "   at Foo.Bar()\n" +
                "   at Foo.Baz()\n";
            log.Append(exceptionText);

            var content = File.ReadAllText(path);
            Assert.Equal(4, CountPhysicalLines(content));
            var lines = PhysicalLines(content);
            Assert.Equal("header", lines[0]);
            Assert.Equal("System.InvalidOperationException: boom", lines[1]);
            Assert.Equal("   at Foo.Bar()", lines[2]);
            Assert.Equal("   at Foo.Baz()", lines[3]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Lf_and_crlf_count_physical_newline_lines()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n");
            var log = new DysonLineCappedLogFile(path);
            log.Append("four\r\n");

            var content = File.ReadAllText(path);
            Assert.Equal(4, CountPhysicalLines(content));
            var lines = PhysicalLines(content);
            Assert.Equal("one\r", lines[0]);
            Assert.Equal("two\r", lines[1]);
            Assert.Equal("three\r", lines[2]);
            Assert.Equal("four\r", lines[3]);
            Assert.Equal("one\r\ntwo\r\nthree\r\nfour\r\n", content);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Pre_existing_over_cap_file_is_trimmed_on_first_append()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            File.WriteAllText(path, Lines(5100, "old-"));
            var log = new DysonLineCappedLogFile(path);
            log.Append("newest\n");

            var content = File.ReadAllText(path);
            var lines = PhysicalLines(content);
            Assert.Equal(DysonLineCappedLogFile.MaxLines, lines.Length);
            Assert.Equal("old-101", lines[0]);
            Assert.Equal("newest", lines[^1]);
            Assert.DoesNotContain("old-0\n", content);
            Assert.DoesNotContain("old-100\n", content);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string Lines(int count, string prefix)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
            builder.Append(prefix).Append(i).Append('\n');
        return builder.ToString();
    }

    private static int CountPhysicalLines(string text)
    {
        if (text.Length == 0)
            return 0;

        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                count++;
        }

        if (text[^1] != '\n')
            count++;

        return count;
    }

    private static string[] PhysicalLines(string text)
    {
        if (text.Length == 0)
            return [];

        var parts = text.Split('\n');
        return parts[^1].Length == 0 ? parts[..^1] : parts;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dyson-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}

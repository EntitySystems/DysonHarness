using DysonHarness;
using Harness.UI.Logging;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

public class DysonFileLoggerProviderTests
{
    [Fact]
    public void LogInformation_writes_nothing()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            using var provider = new DysonFileLoggerProvider(path);
            var logger = provider.CreateLogger("Test.Category");

            logger.LogInformation("hello");
            logger.LogWarning("also ignored");

            Assert.False(File.Exists(path));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void LogError_writes_header_and_exception_ToString()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            using var provider = new DysonFileLoggerProvider(path);
            var logger = provider.CreateLogger("Test.Category");
            var exception = new InvalidOperationException("boom");

            logger.LogError(exception, "failed {Where}", "here");

            var content = File.ReadAllText(path);
            Assert.Matches(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[Error\] Test\.Category: failed here\r?\n",
                content);
            Assert.Contains(exception.ToString(), content);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void LogCritical_writes()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            using var provider = new DysonFileLoggerProvider(path);
            var logger = provider.CreateLogger("Test.Category");

            logger.LogCritical("fatal");

            var content = File.ReadAllText(path);
            Assert.Contains("[Critical] Test.Category: fatal", content);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Many_error_writes_keep_file_at_or_under_line_cap()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "dyson.log");
            using var provider = new DysonFileLoggerProvider(path);
            var logger = provider.CreateLogger("Test.Category");

            for (var i = 0; i < DysonLineCappedLogFile.MaxLines + 50; i++)
                logger.LogError("line {I}", i);

            var content = File.ReadAllText(path);
            var lines = PhysicalLines(content);
            Assert.True(lines.Length <= DysonLineCappedLogFile.MaxLines);
            Assert.Equal(DysonLineCappedLogFile.MaxLines, lines.Length);
            Assert.EndsWith("line 5049", lines[^1]);
            Assert.DoesNotContain("line 0\n", content.Replace("\r\n", "\n"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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
        var path = Path.Combine(Path.GetTempPath(), $"dyson-file-logger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}

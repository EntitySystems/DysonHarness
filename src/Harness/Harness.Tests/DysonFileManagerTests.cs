using System.Text.RegularExpressions;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Slug/hash naming + plans path sandbox (Xunit).</summary>
public class DysonFileManagerTests
{
    [Fact]
    public void Run()
    {
        AssertSanitizeSlug();
        AssertWriteNewPlanNamingAndSandbox();
    }

    private static void AssertSanitizeSlug()
    {
        if (DysonFileManager.SanitizeSlug(null) != "plan"
            || DysonFileManager.SanitizeSlug("  ") != "plan"
            || DysonFileManager.SanitizeSlug("Hello World!") != "hello-world"
            || DysonFileManager.SanitizeSlug("Foo_Bar.md") != "foo-bar-md")
        {
            throw new InvalidOperationException("DysonFileManager.SanitizeSlug failed.");
        }
    }

    private static void AssertWriteNewPlanNamingAndSandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-fm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fm = new DysonFileManager(root);
            var markdown = "# Test plan\n\nDo the thing.\n";
            var written = fm.WriteNewPlan("My Cool Plan", markdown);
            if (written.IsError)
                throw new InvalidOperationException(written.Error);

            var rel = written.Value;
            if (!rel.StartsWith(".dyson/plans/", StringComparison.Ordinal)
                || !rel.EndsWith(".md", StringComparison.Ordinal)
                || !Regex.IsMatch(rel, @"^\.dyson/plans/my-cool-plan-[0-9a-f]{10}\.md$"))
            {
                throw new InvalidOperationException($"Unexpected plan relative path: {rel}");
            }

            var abs = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(abs))
                throw new InvalidOperationException("Plan file was not written.");

            var read = fm.ReadText(rel);
            if (read.IsError || read.Value != markdown)
                throw new InvalidOperationException("ReadText round-trip failed.");

            var escape = fm.ReadText("../outside.txt");
            if (escape.IsSuccess)
                throw new InvalidOperationException("Expected path-escape ReadText to fail.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}

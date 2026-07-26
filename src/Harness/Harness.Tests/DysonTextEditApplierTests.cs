using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only WriteFile cascade (exact, CRLF, indent, ambiguous, replace_all, prefix miss).
/// 
/// </summary>
public class DysonTextEditApplierTests
{
    [Fact]
    public void Run()
    {
        AssertCatalog();
        AssertExactHit();
        AssertCrlfFileWithLfOldText();
        AssertIndentDrift();
        AssertAmbiguousFails();
        AssertReplaceAll();
        AssertReadFilePrefixLookingOldTextFailsCleanly();
    }

    private static void AssertCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("WriteFile", out var write)
            || !write.Description.Contains("replace_all", StringComparison.Ordinal)
            || !write.Description.Contains("123|", StringComparison.Ordinal)
            || !write.InputSchemaJson.Contains("replace_all", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "WriteFile catalog must document replace_all and ReadFile prefix guidance.");
        }

        if (!pipeline.Tools.TryGetValue("ReadFile", out var read)
            || !read.Description.Contains("lineNumber|content", StringComparison.Ordinal)
            || !read.Description.Contains("after the first '|'", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ReadFile catalog must document lineNumber|content format for edits.");
        }
    }

    private static void AssertExactHit()
    {
        const string content = "alpha\nbeta\ngamma\n";
        var result = DysonTextEditApplier.TryReplace(content, "beta", "BETA");
        if (result.IsError || result.Value.Content != "alpha\nBETA\ngamma\n" || result.Value.ReplacementCount != 1)
            throw new InvalidOperationException("Exact replace must succeed once.");
    }

    private static void AssertCrlfFileWithLfOldText()
    {
        const string content = "line1\r\nline2\r\nline3\r\n";
        var result = DysonTextEditApplier.TryReplace(content, "line2\n", "LINE2\n");
        if (result.IsError)
            throw new InvalidOperationException($"CRLF file + LF old_text must match: {result.Error.Message}");

        if (result.Value.Content != "line1\r\nLINE2\r\nline3\r\n")
            throw new InvalidOperationException("CRLF file EOL must be preserved on write.");
    }

    private static void AssertIndentDrift()
    {
        const string content = "void M()\n{\n    Console.WriteLine(\"x\");\n}\n";
        // Model omits the 4-space indent on the middle line.
        const string oldText = "void M()\n{\nConsole.WriteLine(\"x\");\n}";
        const string newText = "void M()\n{\n    Console.WriteLine(\"y\");\n}";
        var result = DysonTextEditApplier.TryReplace(content, oldText, newText);
        if (result.IsError)
            throw new InvalidOperationException($"Indent drift must match via cascade: {result.Error.Message}");

        if (!result.Value.Content.Contains("WriteLine(\"y\")", StringComparison.Ordinal))
            throw new InvalidOperationException("Indent-flexible replace must apply new_text.");
    }

    private static void AssertAmbiguousFails()
    {
        const string content = "foo\nbar\nfoo\n";
        var result = DysonTextEditApplier.TryReplace(content, "foo", "baz", replaceAll: false);
        if (result.IsSuccess || result.Error.Kind != DysonTextEditApplier.FailureKind.Ambiguous)
            throw new InvalidOperationException("Duplicate old_text must fail Ambiguous without replace_all.");

        if (!result.Error.Message.Contains("replace_all", StringComparison.Ordinal))
            throw new InvalidOperationException("Ambiguous error must hint replace_all.");
    }

    private static void AssertReplaceAll()
    {
        const string content = "foo\nbar\nfoo\n";
        var result = DysonTextEditApplier.TryReplace(content, "foo", "baz", replaceAll: true);
        if (result.IsError || result.Value.Content != "baz\nbar\nbaz\n" || result.Value.ReplacementCount != 2)
            throw new InvalidOperationException("replace_all must replace every exact occurrence.");
    }

    private static void AssertReadFilePrefixLookingOldTextFailsCleanly()
    {
        const string content = "    foo();\n    bar();\n";
        // Looks like a pasted ReadFile line; not present in the file as-is.
        const string oldText = "12|    foo();";
        var result = DysonTextEditApplier.TryReplace(content, oldText, "12|    baz();");
        if (result.IsSuccess || result.Error.Kind != DysonTextEditApplier.FailureKind.NotFound)
            throw new InvalidOperationException("ReadFile-prefix old_text must fail NotFound cleanly.");

        if (!result.Error.Message.Contains("123|", StringComparison.Ordinal)
            && !result.Error.Message.Contains("line-number", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NotFound must hint stripping ReadFile line prefixes.");
        }
    }
}

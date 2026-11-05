namespace Harness.Tests;

/// <summary>
/// Razor markup emits a line starting with <c>#if</c> / <c>#else</c> / <c>#elif</c> / <c>#endif</c>
/// as text. Those directives are only valid inside an <c>@code</c> block (or a .cs file).
/// </summary>
public class DysonRazorPreprocessorScanTests
{
    private static readonly string[] Directives = ["#if", "#else", "#elif", "#endif"];

    [Fact]
    public void Razor_markup_has_no_preprocessor_directives()
    {
        var root = FindHarnessRoot();
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inCode = false;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!inCode && trimmed.StartsWith("@code", StringComparison.Ordinal))
                {
                    inCode = true;
                    continue;
                }

                if (inCode)
                {
                    if (trimmed.StartsWith('}'))
                        inCode = false;
                    continue;
                }

                foreach (var directive in Directives)
                {
                    if (trimmed.StartsWith(directive, StringComparison.Ordinal)
                        && (trimmed.Length == directive.Length || char.IsWhiteSpace(trimmed[directive.Length])))
                    {
                        failures.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {trimmed}");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindHarnessRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Harness");
            if (Directory.Exists(Path.Combine(candidate, "Harness.UI")))
                return candidate;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/Harness from the test output directory.");
    }
}

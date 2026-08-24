using ColorCode;
using Harness.UI.Markdown;

namespace Harness.Tests;

public class ColorCodeLanguagesTests
{
    [Fact]
    public void All_contains_every_color_code_language()
    {
        Assert.Equal(25, ColorCodeLanguages.All.Count);

        var catalogIds = ColorCodeLanguages.All
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var colorCodeIds = Languages.All
            .Select(language => language.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(colorCodeIds, catalogIds);
    }

    [Fact]
    public void Every_alias_and_file_extension_resolves_to_its_catalog_entry()
    {
        foreach (var entry in ColorCodeLanguages.All)
        {
            foreach (var alias in entry.Aliases)
                Assert.True(
                    string.Equals(entry.Id, ColorCodeLanguages.TryResolve(alias)?.Id, StringComparison.OrdinalIgnoreCase),
                    $"Alias '{alias}' for {entry.Id}");

            foreach (var extension in entry.FileExtensions)
            {
                Assert.Equal(entry.Id, ColorCodeLanguages.TryResolve(extension)?.Id);
                Assert.Equal(entry.Id, ColorCodeLanguages.TryResolve(extension.TrimStart('.'))?.Id);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("go")]
    [InlineData("yaml")]
    [InlineData("bash")]
    [InlineData(".rs")]
    public void TryResolve_returns_null_for_unknown_or_empty_input(string? input)
    {
        Assert.Null(ColorCodeLanguages.TryResolve(input));
    }

    [Fact]
    public void TryResolve_uses_the_first_fence_info_token()
    {
        Assert.Equal("c#", ColorCodeLanguages.TryResolve("csharp title=\"x\"")?.Id);
    }

    [Fact]
    public void TryResolve_is_case_insensitive_for_aliases_and_extensions()
    {
        Assert.Equal("c#", ColorCodeLanguages.TryResolve(".CS")?.Id);
        Assert.Equal("c#", ColorCodeLanguages.TryResolve("cs")?.Id);
    }

    [Fact]
    public void TryResolve_keeps_source_languages_disjoint_from_msbuild_project_xml()
    {
        Assert.Equal("c#", ColorCodeLanguages.TryResolve(".cs")?.Id);
        Assert.Equal("c#", ColorCodeLanguages.TryResolve("cs")?.Id);
        Assert.Equal("xml", ColorCodeLanguages.TryResolve(".csproj")?.Id);
        Assert.Equal("xml", ColorCodeLanguages.TryResolve("csproj")?.Id);

        Assert.Equal("f#", ColorCodeLanguages.TryResolve(".fs")?.Id);
        Assert.Equal("f#", ColorCodeLanguages.TryResolve("fs")?.Id);
        Assert.Equal("xml", ColorCodeLanguages.TryResolve(".fsproj")?.Id);
        Assert.Equal("xml", ColorCodeLanguages.TryResolve("fsproj")?.Id);

        Assert.Equal("vb.net", ColorCodeLanguages.TryResolve(".vb")?.Id);
        Assert.Equal("xml", ColorCodeLanguages.TryResolve(".vbproj")?.Id);
    }
}

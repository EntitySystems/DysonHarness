using ColorCode;

namespace Harness.UI.Markdown;

public static class ColorCodeLanguages
{
    public sealed record Entry(
        string Id,
        string DisplayName,
        string CssClassName,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> FileExtensions);

    private static readonly IReadOnlyList<Entry> Entries = Array.AsReadOnly<Entry>(
    [
        new("c#", "C#", "csharp", ["cs", "c#", "csharp", "cake"], [".cs", ".csx", ".cake"]),
        new("f#", "F#", "FSharp", ["fs", "f#", "fsharp"], [".fs", ".fsi", ".fsx"]),
        new("vb.net", "VB.NET", "vb-net", ["vb.net", "vbnet", "vb", "visualbasic", "visual basic"], [".vb"]),
        new("cpp", "C++", "cplusplus", ["c++", "c"], [".cpp", ".cc", ".cxx", ".h", ".hpp", ".c"]),
        new("java", "Java", "java", [], [".java"]),
        new("javascript", "JavaScript", "javascript", ["js"], [".js", ".mjs", ".cjs"]),
        new("typescript", "TypeScript", "typescript", ["ts"], [".ts", ".mts", ".cts"]),
        new("json", "JSON", "json", [], [".json"]),
        new("html", "HTML", "html", ["htm"], [".html", ".htm"]),
        new("xml", "XML", "xml", ["xaml", "axml"], [".xml", ".xaml", ".axml"]),
        new("css", "CSS", "css", [], [".css"]),
        new("sql", "SQL", "sql", [], [".sql"]),
        new("php", "PHP", "php", ["php3", "php4", "php5"], [".php"]),
        new("powershell", "PowerShell", "powershell", ["posh", "ps1", "pwsh"], [".ps1", ".psm1", ".psd1"]),
        new("python", "Python", "python", ["py", "python"], [".py"]),
        new("markdown", "Markdown", "markdown", ["md", "markdown"], [".md", ".markdown"]),
        new("fortran", "Fortran", "fortran", ["fortran"], [".f", ".for", ".f90"]),
        new("haskell", "Haskell", "haskell", ["hs"], [".hs"]),
        new("koka", "Koka", "koka", ["kk", "kki"], [".kk", ".kki"]),
        new("matlab", "MATLAB", "matlab", ["m", "mat", "matlab"], [".m"]),
        new("asax", "ASAX", "asax", [], [".asax"]),
        new("ashx", "ASHX", "ashx", [], [".ashx"]),
        new("aspx", "ASPX", "aspx", [], [".aspx"]),
        new("aspx(c#)", "ASPX (C#)", "aspx-cs", ["aspx-cs", "aspx (cs)", "aspx(cs)"], []),
        new("aspx(vb.net)", "ASPX (VB.NET)", "aspx-vb", ["aspx-vb", "aspx (vb.net)", "aspx(vb.net)"], []),
    ]);

    public static IReadOnlyList<Entry> All => Entries;

    /// <summary>
    /// Fence info (first token) or file extension (with or without dot).
    /// Uses ColorCode FindById (Id + HasAlias), then Entry.FileExtensions.
    /// </summary>
    public static ILanguage? TryResolve(string? fenceInfoOrExtension)
    {
        if (string.IsNullOrWhiteSpace(fenceInfoOrExtension))
            return null;

        var token = fenceInfoOrExtension.Trim();
        if (token.StartsWith(".", StringComparison.Ordinal))
            token = token[1..];

        if (string.IsNullOrEmpty(token))
            return null;

        // Preserve aliases that intentionally contain spaces (for example, "visual basic")
        // before applying fence-info tokenization.
        var exactAlias = Entries.FirstOrDefault(candidate => candidate.Aliases.Any(alias =>
            string.Equals(alias, token, StringComparison.OrdinalIgnoreCase)));
        if (exactAlias is not null)
            return Languages.FindById(exactAlias.Id);

        var separator = token.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator >= 0)
            token = token[..separator];

        if (string.IsNullOrEmpty(token))
            return null;

        var language = Languages.FindById(token);
        if (language is not null)
            return language;

        var entry = Entries.FirstOrDefault(candidate => candidate.FileExtensions.Any(extension =>
            string.Equals(extension.TrimStart('.'), token, StringComparison.OrdinalIgnoreCase)));

        return entry is null ? null : Languages.FindById(entry.Id);
    }
}

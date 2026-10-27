namespace DysonHarness;

/// <summary>
/// Display-path scheme for DB-backed Meta scratch files in the file viewer
/// (<c>metascratch:{id}/{slug}.md</c>). The <c>.md</c> suffix makes the viewer render markdown.
/// Not a filesystem path.
/// </summary>
public static class DysonMetaScratchDisplayPath
{
    public const string Prefix = "metascratch:";

    public static string Format(long id, string? title)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return $"{Prefix}{id}/{DysonFileManager.SanitizeSlug(title)}.md";
    }
}

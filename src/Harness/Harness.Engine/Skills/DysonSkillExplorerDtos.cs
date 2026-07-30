namespace DysonHarness;

/// <summary>Registered skill explorer provider (tab id + label).</summary>
public sealed record DysonSkillExplorerProviderInfo(string Name, string DisplayName);

/// <summary>Provider-neutral skill catalog row.</summary>
public sealed record DysonSkillExplorerEntry(
    string Slug,
    string Name,
    string Description,
    string? Author,
    int Stars,
    bool Verified,
    IReadOnlyList<string> Tags);

/// <summary>Paginated search page from a skill explorer provider.</summary>
public sealed record DysonSkillExplorerSearchPage(
    IReadOnlyList<DysonSkillExplorerEntry> Skills,
    int Total,
    int Limit,
    int Offset,
    bool HasMore);

/// <summary>
/// One ambiguous publisher/ref match. <see cref="Slug"/> is the retry id
/// (<c>owner/slug</c>); <see cref="Label"/> is display (prefer <c>ref</c>).
/// </summary>
public sealed record DysonSkillExplorerMatch(
    string Slug,
    string Label,
    string? OwnerHandle,
    string? Ref);

/// <summary>Download result: installed path or ambiguous publisher matches.</summary>
public abstract record DysonSkillExplorerDownloadOutcome
{
    private DysonSkillExplorerDownloadOutcome()
    {
    }

    public sealed record Installed(string RelativePath) : DysonSkillExplorerDownloadOutcome;

    public sealed record Ambiguous(IReadOnlyList<DysonSkillExplorerMatch> Matches)
        : DysonSkillExplorerDownloadOutcome;
}

/// <summary>Preview result: markdown body or ambiguous publisher matches.</summary>
public abstract record DysonSkillExplorerPreviewOutcome
{
    private DysonSkillExplorerPreviewOutcome()
    {
    }

    public sealed record Markdown(string Content) : DysonSkillExplorerPreviewOutcome;

    public sealed record Ambiguous(IReadOnlyList<DysonSkillExplorerMatch> Matches)
        : DysonSkillExplorerPreviewOutcome;
}

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

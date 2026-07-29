namespace DysonHarness;

/// <summary>Slug row for managed-provider catalog sync.</summary>
public sealed record ManagedSlugSpec(
    string Slug,
    string DisplayAlias,
    string? DefaultReasoningEffort,
    IReadOnlyList<string> ReasoningModes);

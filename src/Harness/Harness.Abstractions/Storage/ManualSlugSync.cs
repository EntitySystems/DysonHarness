namespace DysonHarness;

/// <summary>Added, removed, and kept slug counts from a manual catalog sync.</summary>
public sealed record ManualSlugSync(int Added, int Removed, int Kept);

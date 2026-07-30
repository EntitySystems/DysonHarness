namespace DysonHarness;

/// <summary>
/// One skill catalog/install backend. Registered under <see cref="ProviderName"/>;
/// <see cref="IDysonSkillExplorer"/> routes by that name.
/// </summary>
public interface IDysonSkillExplorerProvider
{
    string ProviderName { get; }

    string DisplayName { get; }

    Task<Result<DysonSkillExplorerSearchPage, string>> SearchAsync(
        string? query,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<Result<DysonSkillExplorerEntry, string>> GetAsync(
        string slug,
        CancellationToken cancellationToken = default);

    /// <summary>Fetch SKILL.md text for preview without installing.</summary>
    Task<Result<DysonSkillExplorerPreviewOutcome, string>> PreviewSkillMarkdownAsync(
        string slug,
        CancellationToken cancellationToken = default);

    /// <summary>Install skill package into workdir <c>.dyson/skills/{slug}/</c>.</summary>
    Task<Result<DysonSkillExplorerDownloadOutcome, string>> DownloadAsync(
        string slug,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default);
}

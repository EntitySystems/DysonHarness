namespace DysonHarness;

/// <summary>
/// Provider-agnostic skill explorer facade. Every operation takes
/// <paramref name="providerName"/> and routes case-insensitively.
/// </summary>
public interface IDysonSkillExplorer
{
    /// <summary>Registered providers (stable order). <see cref="DysonSkillExplorerProviderInfo.Name"/> is the routing key.</summary>
    IReadOnlyList<DysonSkillExplorerProviderInfo> ListProviders();

    Task<Result<DysonSkillExplorerSearchPage, string>> SearchAsync(
        string providerName,
        string? query,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<Result<DysonSkillExplorerEntry, string>> GetAsync(
        string providerName,
        string slug,
        CancellationToken cancellationToken = default);

    /// <summary>Fetch SKILL.md text for preview without installing (provider-specific).</summary>
    Task<Result<DysonSkillExplorerPreviewOutcome, string>> PreviewSkillMarkdownAsync(
        string providerName,
        string slug,
        CancellationToken cancellationToken = default);

    /// <summary>Install skill package into workdir <c>.dyson/skills/{slug}/</c>.</summary>
    Task<Result<DysonSkillExplorerDownloadOutcome, string>> DownloadAsync(
        string providerName,
        string slug,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default);
}

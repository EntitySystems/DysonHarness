namespace DysonHarness;

/// <summary>Routes skill explorer calls to registered <see cref="IDysonSkillExplorerProvider"/>s.</summary>
public sealed class DysonSkillExplorer : IDysonSkillExplorer
{
    private readonly IReadOnlyDictionary<string, IDysonSkillExplorerProvider> _providers;
    private readonly IReadOnlyList<DysonSkillExplorerProviderInfo> _providerInfos;

    public DysonSkillExplorer(IEnumerable<IDysonSkillExplorerProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<string, IDysonSkillExplorerProvider>(StringComparer.OrdinalIgnoreCase);
        var infos = new List<DysonSkillExplorerProviderInfo>();
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.ProviderName))
                throw new ArgumentException("ProviderName is required.", nameof(providers));

            var key = provider.ProviderName.Trim();
            if (!map.TryAdd(key, provider))
                throw new ArgumentException($"Duplicate skill explorer provider '{key}'.", nameof(providers));

            infos.Add(new DysonSkillExplorerProviderInfo(key, provider.DisplayName));
        }

        _providers = map;
        _providerInfos = infos;
    }

    public IReadOnlyList<DysonSkillExplorerProviderInfo> ListProviders() => _providerInfos;

    public Task<Result<DysonSkillExplorerSearchPage, string>> SearchAsync(
        string providerName,
        string? query,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(providerName);
        if (resolved.IsError)
            return Task.FromResult(Result<DysonSkillExplorerSearchPage, string>.AsError(resolved.Error));
        return resolved.Value.SearchAsync(query, limit, offset, cancellationToken);
    }

    public Task<Result<DysonSkillExplorerEntry, string>> GetAsync(
        string providerName,
        string slug,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(providerName);
        if (resolved.IsError)
            return Task.FromResult(Result<DysonSkillExplorerEntry, string>.AsError(resolved.Error));
        return resolved.Value.GetAsync(slug, cancellationToken);
    }

    public Task<Result<DysonSkillExplorerPreviewOutcome, string>> PreviewSkillMarkdownAsync(
        string providerName,
        string slug,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(providerName);
        if (resolved.IsError)
            return Task.FromResult(Result<DysonSkillExplorerPreviewOutcome, string>.AsError(resolved.Error));
        return resolved.Value.PreviewSkillMarkdownAsync(slug, cancellationToken);
    }

    public Task<Result<DysonSkillExplorerDownloadOutcome, string>> DownloadAsync(
        string providerName,
        string slug,
        IDysonWorkspaceFileSystem fs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        var resolved = Resolve(providerName);
        if (resolved.IsError)
            return Task.FromResult(Result<DysonSkillExplorerDownloadOutcome, string>.AsError(resolved.Error));
        return resolved.Value.DownloadAsync(slug, fs, cancellationToken);
    }

    private Result<IDysonSkillExplorerProvider, string> Resolve(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return Result<IDysonSkillExplorerProvider, string>.AsError("providerName is required.");

        if (_providers.TryGetValue(providerName.Trim(), out var provider))
            return Result<IDysonSkillExplorerProvider, string>.AsValue(provider);

        return Result<IDysonSkillExplorerProvider, string>.AsError(
            $"Unknown skill explorer provider '{providerName.Trim()}'.");
    }
}

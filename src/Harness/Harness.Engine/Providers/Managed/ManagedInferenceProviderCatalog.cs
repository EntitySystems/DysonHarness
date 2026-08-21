namespace DysonHarness;

/// <summary>Scoped catalog of managed inference providers for Settings → Models.</summary>
public sealed class ManagedInferenceProviderCatalog
{
    public ManagedInferenceProviderCatalog(
        DysonCliProxyHost host,
        HttpClient http,
        IDysonModelRepository models,
        IDysonSubjectSettingsRepository subjectSettings)
    {
        All =
        [
            new ManagedCodexInferenceProvider(host, http, models, subjectSettings),
            new ManagedGrokInferenceProvider(host, http, models, subjectSettings),
            new ManagedAntigravityInferenceProvider(host, http, models, subjectSettings),
            new ManagedKimiInferenceProvider(host, http, models, subjectSettings),
            new ManagedClaudeInferenceProvider(host, http, models, subjectSettings),
        ];
        Direct = [new OpenRouterManagedInferenceProvider(http, models)];
    }

    public IReadOnlyList<ManagedInferenceProviderBase> All { get; }

    /// <summary>Direct API-key managed providers (OpenRouter); not in <see cref="All"/>.</summary>
    public IReadOnlyList<IManagedInferenceProvider> Direct { get; }

    public ManagedInferenceProviderBase? FindBySource(string? managedSource)
    {
        if (string.IsNullOrWhiteSpace(managedSource))
            return null;

        return All.FirstOrDefault(p =>
            string.Equals(p.ManagedSource, managedSource, StringComparison.Ordinal));
    }

    public IManagedInferenceProvider? FindDirectBySource(string? managedSource)
    {
        if (string.IsNullOrWhiteSpace(managedSource))
            return null;

        return Direct.FirstOrDefault(p =>
            string.Equals(p.ManagedSource, managedSource, StringComparison.Ordinal));
    }
}

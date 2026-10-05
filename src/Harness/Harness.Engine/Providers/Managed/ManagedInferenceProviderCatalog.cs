namespace DysonHarness;

/// <summary>Scoped catalog of managed inference providers for Settings → Models.</summary>
public sealed class ManagedInferenceProviderCatalog
{
    public ManagedInferenceProviderCatalog(
        DysonCliProxyHost host,
        HttpClient http,
        IDysonModelRepository models,
        IDysonSubjectSettingsRepository subjectSettings,
        XaiGrokAuthService xaiAuth,
        IDysonSubjectContext subject,
        XaiGrokClientOptions? xaiOptions = null)
    {
        XaiGrok = new XaiGrokManagedInferenceProvider(xaiAuth, http, models, subject, subjectSettings, xaiOptions);
        All =
        [
            new ManagedCodexInferenceProvider(host, http, models, subjectSettings),
            XaiGrok,
            new ManagedGrokInferenceProvider(host, http, models, subjectSettings),
            new ManagedAntigravityInferenceProvider(host, http, models, subjectSettings),
            new ManagedKimiInferenceProvider(host, http, models, subjectSettings),
            new ManagedClaudeInferenceProvider(host, http, models, subjectSettings),
            new ManagedMetaInferenceProvider(host, http, models, subjectSettings),
        ];
        Direct =
        [
            new OpenRouterManagedInferenceProvider(http, models),
            new OrcaRouterManagedInferenceProvider(http, models),
        ];
    }

    /// <summary>Native xAI/Grok provider (also listed in <see cref="All"/>).</summary>
    public XaiGrokManagedInferenceProvider XaiGrok { get; }

    public IReadOnlyList<IManagedConnectionProvider> All { get; }

    /// <summary>Direct API-key managed providers (OpenRouter, OrcaRouter); not in <see cref="All"/>.</summary>
    public IReadOnlyList<IManagedInferenceProvider> Direct { get; }

    public IManagedConnectionProvider? FindBySource(string? managedSource)
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

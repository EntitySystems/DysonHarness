namespace DysonHarness;

/// <summary>Scoped catalog of managed inference providers for Settings → Models.</summary>
public sealed class ManagedInferenceProviderCatalog
{
    public ManagedInferenceProviderCatalog(
        DysonCliProxyHost host,
        HttpClient http,
        DysonModelStore models,
        DysonAppSettingsStore appSettings)
    {
        All =
        [
            new ManagedCodexInferenceProvider(host, http, models, appSettings),
            new ManagedGrokInferenceProvider(host, http, models, appSettings),
        ];
    }

    public IReadOnlyList<ManagedInferenceProviderBase> All { get; }

    public ManagedInferenceProviderBase? FindBySource(string? managedSource)
    {
        if (string.IsNullOrWhiteSpace(managedSource))
            return null;

        return All.FirstOrDefault(p =>
            string.Equals(p.ManagedSource, managedSource, StringComparison.Ordinal));
    }
}

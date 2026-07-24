namespace DysonHarness;

/// <summary>Known provider-kind strings stored on <see cref="DysonModelProviderEntity.ProviderKind"/>.</summary>
public static class DysonProviderKinds
{
    public const string Demo = "demo";
    public const string OpenAICompatible = "OpenAICompatible";
    public const string Anthropic = "Anthropic";

    public static readonly string[] All = [Demo, OpenAICompatible, Anthropic];

    public static bool HasCredentials(string? baseUrl, string? apiKey) =>
        !string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(apiKey);

    /// <summary>
    /// Credentialed providers tagged <see cref="Demo"/> are treated as <see cref="OpenAICompatible"/>.
    /// </summary>
    public static string EffectiveKind(string providerKind, string? baseUrl, string? apiKey)
    {
        var kind = string.IsNullOrWhiteSpace(providerKind) ? Demo : providerKind;
        if (string.Equals(kind, Demo, StringComparison.Ordinal) && HasCredentials(baseUrl, apiKey))
            return OpenAICompatible;
        return kind;
    }
}

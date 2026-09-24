namespace DysonHarness;

/// <summary>
/// Meta Muse Code via CLIProxy management <c>meta-auth-url</c> (device code, same shape as xAI/Grok).
/// Upstream provider key is <c>meta</c>, so the managed source is <c>cliproxy-meta</c>.
/// </summary>
public sealed class ManagedMetaInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    IDysonModelRepository models,
    IDysonSubjectSettingsRepository? subjectSettings = null)
    : ManagedInferenceProviderBase(host, http, models, subjectSettings)
{
    internal const string MetaAuthUrlPath = "meta-auth-url";

    public override string ManagedSource => DysonManagedSources.CliProxyMeta;
    public override string DisplayName => "Meta Muse (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    protected override string AuthUrlPath => MetaAuthUrlPath;

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["meta", "muse"];
}

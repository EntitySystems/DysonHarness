namespace DysonHarness;

public sealed class ManagedGrokInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    IDysonModelRepository models,
    IDysonSubjectSettingsRepository? subjectSettings = null)
    : ManagedInferenceProviderBase(host, http, models, subjectSettings)
{
    public override string ManagedSource => DysonManagedSources.CliProxyGrok;
    public override string DisplayName => "Grok Build (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    protected override string AuthUrlPath => "xai-auth-url";

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["xai", "x-ai", "grok"];
}

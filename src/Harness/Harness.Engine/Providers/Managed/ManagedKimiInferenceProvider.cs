namespace DysonHarness;

public sealed class ManagedKimiInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    IDysonModelRepository models,
    IDysonSubjectSettingsRepository? subjectSettings = null)
    : ManagedInferenceProviderBase(host, http, models, subjectSettings)
{
    public override string ManagedSource => DysonManagedSources.CliProxyKimi;
    public override string DisplayName => "Kimi (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    protected override string AuthUrlPath => "kimi-auth-url";

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["kimi", "moonshot"];
}

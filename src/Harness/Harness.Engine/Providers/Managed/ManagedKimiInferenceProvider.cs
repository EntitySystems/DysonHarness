namespace DysonHarness;

public sealed class ManagedKimiInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    DysonModelStore models,
    DysonAppSettingsStore? appSettings = null)
    : ManagedInferenceProviderBase(host, http, models, appSettings)
{
    public override string ManagedSource => DysonManagedSources.CliProxyKimi;
    public override string DisplayName => "Kimi (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    protected override string AuthUrlPath => "kimi-auth-url";

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["kimi", "moonshot"];
}

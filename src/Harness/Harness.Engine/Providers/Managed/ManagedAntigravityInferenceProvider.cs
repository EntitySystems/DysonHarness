using System.Net;
using System.Net.Sockets;

namespace DysonHarness;

public sealed class ManagedAntigravityInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    DysonModelStore models,
    DysonAppSettingsStore? appSettings = null)
    : ManagedInferenceProviderBase(host, http, models, appSettings)
{
    /// <summary>Antigravity OAuth web-UI forwarder port (hardcoded by CLIProxy).</summary>
    public const int AntigravityOAuthCallbackPort = 51121;

    internal const string AntigravityAuthUrlPath = "antigravity-auth-url?is_webui=true";

    public override string ManagedSource => DysonManagedSources.CliProxyAntigravity;
    public override string DisplayName => "Antigravity (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    protected override string AuthUrlPath => AntigravityAuthUrlPath;

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["antigravity", "google", "gemini"];

    protected override Task<VoidResult<string>> PreflightBeginConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryEnsureOAuthCallbackPortFree());
    }

    /// <summary>
    /// Bind-check <see cref="AntigravityOAuthCallbackPort"/> so Connect fails visibly when the
    /// CLIProxy OAuth forwarder cannot start.
    /// </summary>
    internal static VoidResult<string> TryEnsureOAuthCallbackPortFree()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, AntigravityOAuthCallbackPort);
            listener.Start();
            listener.Stop();
            return VoidResult<string>.Success;
        }
        catch (SocketException)
        {
            return VoidResult<string>.AsError(
                $"Antigravity OAuth needs localhost port {AntigravityOAuthCallbackPort} free (something else is using it). Close the other app or finish any other Antigravity login, then click Connect again.");
        }
    }
}

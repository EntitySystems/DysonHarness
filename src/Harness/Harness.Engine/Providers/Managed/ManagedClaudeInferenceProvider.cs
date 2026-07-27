using System.Net;
using System.Net.Sockets;

namespace DysonHarness;

public sealed class ManagedClaudeInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    DysonModelStore models,
    DysonAppSettingsStore? appSettings = null)
    : ManagedInferenceProviderBase(host, http, models, appSettings)
{
    /// <summary>Claude Code OAuth web-UI forwarder port (hardcoded by CLIProxy).</summary>
    public const int ClaudeOAuthCallbackPort = 54545;

    internal const string ClaudeAuthUrlPath = "anthropic-auth-url?is_webui=true";

    public override string ManagedSource => DysonManagedSources.CliProxyClaude;
    public override string DisplayName => "Claude Code (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    protected override string AuthUrlPath => ClaudeAuthUrlPath;

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["claude", "anthropic"];

    protected override Task<VoidResult<string>> PreflightBeginConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryEnsureOAuthCallbackPortFree());
    }

    /// <summary>
    /// Bind-check <see cref="ClaudeOAuthCallbackPort"/> so Connect fails visibly when the
    /// CLIProxy OAuth forwarder cannot start.
    /// </summary>
    internal static VoidResult<string> TryEnsureOAuthCallbackPortFree()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, ClaudeOAuthCallbackPort);
            listener.Start();
            listener.Stop();
            return VoidResult<string>.Success;
        }
        catch (SocketException)
        {
            return VoidResult<string>.AsError(
                $"Claude Code OAuth needs localhost port {ClaudeOAuthCallbackPort} free (something else is using it). Close the other app or finish any other Claude login, then click Connect again.");
        }
    }
}

using System.Net;
using System.Net.Sockets;

namespace DysonHarness;

public sealed class ManagedCodexInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    IDysonModelRepository models,
    IDysonSubjectSettingsRepository? subjectSettings = null)
    : ManagedInferenceProviderBase(host, http, models, subjectSettings)
{
    /// <summary>Codex OAuth redirect port (hardcoded by OpenAI / CLIProxy web-UI forwarder).</summary>
    public const int CodexOAuthCallbackPort = 1455;

    internal const string CodexAuthUrlPath = "codex-auth-url?is_webui=true";

    public override string ManagedSource => DysonManagedSources.CliProxyCodex;
    public override string DisplayName => "ChatGPT Codex (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    protected override string AuthUrlPath => CodexAuthUrlPath;

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["codex", "openai"];

    protected override Task<VoidResult<string>> PreflightBeginConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryEnsureOAuthCallbackPortFree());
    }

    /// <summary>
    /// Bind-check <see cref="CodexOAuthCallbackPort"/> so Connect fails visibly when the
    /// CLIProxy OAuth forwarder cannot start.
    /// </summary>
    internal static VoidResult<string> TryEnsureOAuthCallbackPortFree()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, CodexOAuthCallbackPort);
            listener.Start();
            listener.Stop();
            return VoidResult<string>.Success;
        }
        catch (SocketException)
        {
            return VoidResult<string>.AsError(
                $"Codex OAuth needs localhost port {CodexOAuthCallbackPort} free (something else is using it). Close the other app or finish any other Codex login, then click Connect again.");
        }
    }
}

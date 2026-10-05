// Derived from router-for-me/CLIProxyAPI (MIT)
//   internal/auth/xai/types.go, internal/runtime/executor/xai_executor_request.go @ 97f244b8ddb9cbf564b6e6faab0159102cca8617.
// See THIRD-PARTY-NOTICES.md.

namespace DysonHarness;

/// <summary>
/// Host-level overrides for the native xAI client identity, bound from config section
/// <see cref="SectionName"/> (env <c>Dyson__XaiGrok__ClientVersion</c>). Not stored per subject.
/// </summary>
public sealed class XaiGrokClientOptions
{
    public const string SectionName = "Dyson:XaiGrok";

    /// <summary>Overrides <see cref="XaiGrokClientProfile.DefaultClientVersion"/> (xAI answers 426 below its minimum).</summary>
    public string? ClientVersion { get; set; }

    /// <summary>Extra/overriding request headers; these win over the defaults (CLIProxy custom-header precedence).</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The one place that owns xAI chat-proxy identity constants and the header set. Bump
/// <see cref="DefaultClientVersion"/> here when xAI raises its minimum client version.
/// </summary>
public static class XaiGrokClientProfile
{
    public const string DefaultClientVersion = "1.0.13";
    public const string UserAgentPrefix = "xai-grok-workspace/";
    public const string ClientIdentifier = "grok-shell";
    public const string TokenAuthValue = "xai-grok-cli";
    public const string AuthenticateResponseValue = "authenticate-response";

    public const string ChatProxyBaseUrl = "https://cli-chat-proxy.grok.com/v1";
    public const string ChatProxyHost = "cli-chat-proxy.grok.com";
    public const string Issuer = "https://auth.x.ai";
    public const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";
    public const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    public const string Scope = "openid profile email offline_access grok-cli:access api:access";
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Refresh when the access token is within this window of expiry.</summary>
    public static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(5);

    /// <summary>Bounds device/token/refresh HTTP calls.</summary>
    public static readonly TimeSpan OAuthHttpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Minimum device-code poll interval (and the <c>slow_down</c> step).</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on waiting for the user to authorize a device code.</summary>
    public static readonly TimeSpan MaxPollDuration = TimeSpan.FromMinutes(30);

    /// <summary>Version sent as <c>x-grok-client-version</c> and inside the User-Agent.</summary>
    public static string EffectiveVersion(XaiGrokClientOptions? options) =>
        string.IsNullOrWhiteSpace(options?.ClientVersion)
            ? DefaultClientVersion
            : options.ClientVersion.Trim();

    /// <summary>
    /// Identity headers for chat-proxy requests (everything except <c>Authorization</c>). Both version
    /// headers derive from one value so they cannot disagree. <see cref="XaiGrokClientOptions.ExtraHeaders"/> win.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildHeaders(
        XaiGrokClientOptions? options,
        string? conversationId = null,
        bool stream = true)
    {
        var version = EffectiveVersion(options);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-XAI-Token-Auth"] = TokenAuthValue,
            ["x-grok-client-version"] = version,
            ["User-Agent"] = UserAgentPrefix + version,
            ["x-grok-client-identifier"] = ClientIdentifier,
            ["x-authenticateresponse"] = AuthenticateResponseValue,
            ["Accept"] = stream ? "text/event-stream" : "application/json",
            ["Connection"] = "Keep-Alive",
        };

        if (!string.IsNullOrWhiteSpace(conversationId))
            headers["x-grok-conv-id"] = conversationId.Trim();

        if (options?.ExtraHeaders is { Count: > 0 } extra)
        {
            foreach (var (name, value) in extra)
            {
                if (!string.IsNullOrWhiteSpace(name) && value is not null
                    && !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    headers[name.Trim()] = value;
                }
            }
        }

        return headers;
    }
}

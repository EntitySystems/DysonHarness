using DysonHarness;

namespace Harness.Tests;

public class XaiGrokClientProfileTests
{
    [Fact]
    public void Constants_match_documented_values()
    {
        Assert.Equal("1.0.13", XaiGrokClientProfile.DefaultClientVersion);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", XaiGrokClientProfile.ChatProxyBaseUrl);
        Assert.Equal("b1a00492-073a-47ea-816f-4c329264a828", XaiGrokClientProfile.ClientId);
        Assert.Equal("openid profile email offline_access grok-cli:access api:access", XaiGrokClientProfile.Scope);
        Assert.Equal("https://auth.x.ai/.well-known/openid-configuration", XaiGrokClientProfile.DiscoveryUrl);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", XaiGrokClientProfile.DeviceCodeGrantType);
        Assert.Equal(TimeSpan.FromMinutes(5), XaiGrokClientProfile.RefreshLead);
    }

    [Fact]
    public void BuildHeaders_pins_full_set_and_derives_both_versions_from_one_value()
    {
        var h = XaiGrokClientProfile.BuildHeaders(null, "conv-1");

        Assert.Equal("xai-grok-cli", h["X-XAI-Token-Auth"]);
        Assert.Equal("1.0.13", h["x-grok-client-version"]);
        Assert.Equal("xai-grok-workspace/1.0.13", h["User-Agent"]);
        Assert.Equal("grok-shell", h["x-grok-client-identifier"]);
        Assert.Equal("authenticate-response", h["x-authenticateresponse"]);
        Assert.Equal("conv-1", h["x-grok-conv-id"]);
        Assert.Equal("text/event-stream", h["Accept"]);
        Assert.Equal("Keep-Alive", h["Connection"]);
        Assert.Equal(8, h.Count);
    }

    [Fact]
    public void BuildHeaders_omits_conv_id_when_blank_and_uses_json_accept_when_not_streaming()
    {
        var h = XaiGrokClientProfile.BuildHeaders(null, "  ", stream: false);

        Assert.False(h.ContainsKey("x-grok-conv-id"));
        Assert.Equal("application/json", h["Accept"]);
    }

    [Fact]
    public void ClientVersion_override_changes_both_version_headers()
    {
        var options = new XaiGrokClientOptions { ClientVersion = " 2.3.4 " };
        var h = XaiGrokClientProfile.BuildHeaders(options);

        Assert.Equal("2.3.4", h["x-grok-client-version"]);
        Assert.Equal("xai-grok-workspace/2.3.4", h["User-Agent"]);
    }

    [Fact]
    public void ExtraHeaders_win_over_defaults_but_never_replace_Authorization()
    {
        var options = new XaiGrokClientOptions();
        options.ExtraHeaders["user-agent"] = "custom/9";
        options.ExtraHeaders["X-Extra"] = "1";
        options.ExtraHeaders["Authorization"] = "Bearer leaked";

        var h = XaiGrokClientProfile.BuildHeaders(options);

        Assert.Equal("custom/9", h["User-Agent"]);
        Assert.Equal("1", h["X-Extra"]);
        Assert.False(h.ContainsKey("Authorization"));
    }
}

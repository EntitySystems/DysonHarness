namespace DysonHarness;

/// <summary>Known <see cref="DysonAppSettingEntity.Key"/> values.</summary>
public static class DysonAppSettingKeys
{
    /// <summary>
    /// Guid string of the model slug used for web-search/fetch summarization.
    /// Empty / missing ⇒ use the session model.
    /// </summary>
    public const string WebSearchSummarizerModelSlugId = "web_search_summarizer_model_slug_id";

    /// <summary>
    /// Chat tools column width as a percent of the turn content row (e.g. "30").
    /// Empty / missing ⇒ default 30%.
    /// </summary>
    public const string ToolPanelWidthPercent = "tool_panel_width_percent";

    /// <summary>
    /// JSON <see cref="DysonToolPolicyDocument"/> — per-mode (and future per-model) tool denylists.
    /// Empty / missing ⇒ all tools enabled.
    /// </summary>
    public const string AgentModeToolPolicy = "agent_mode_tool_policy";

    /// <summary>
    /// Local CLIProxyAPI Bearer key for <c>/v1/*</c> (mirrored from
    /// <c>external/cliproxy/keys.json</c> when a managed provider connects).
    /// </summary>
    public const string CliProxyApiKey = "cliproxy_api_key";

    /// <summary>
    /// Local CLIProxyAPI management Bearer key for <c>/v0/management/*</c>.
    /// </summary>
    public const string CliProxyManagementKey = "cliproxy_management_key";

    /// <summary>Local CLIProxyAPI listen port (default 8317).</summary>
    public const string CliProxyPort = "cliproxy_port";
}

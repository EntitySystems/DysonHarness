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
    /// Right rail (files/git) open preference: <c>"true"</c> / <c>"false"</c>.
    /// Restored on desktop hydrate; narrow viewports still start closed (drawer UX)
    /// and re-apply this when returning to desktop. Missing ⇒ <c>true</c>.
    /// </summary>
    public const string UiRailOpen = "ui_rail_open";

    /// <summary>
    /// Left sidebar open preference: <c>"true"</c> / <c>"false"</c> (collapsed when false).
    /// Restored on AppShell hydrate. Missing ⇒ <c>true</c>.
    /// </summary>
    public const string UiSidebarOpen = "ui_sidebar_open";

    /// <summary>
    /// JSON agent-mode tool policy document — per-mode (and future per-model) tool denylists.
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

    /// <summary>
    /// When <c>"true"</c>, after an agent marks a task completed a reviewer agent should
    /// auto-run (persist only for now — no reviewer spawn yet). Missing / other ⇒ off.
    /// </summary>
    public const string EndOfTaskAutoReview = "end_of_task_auto_review";

    /// <summary>
    /// Self-review intensity: <c>low</c> / <c>medium</c> / <c>high</c>.
    /// Persist only for now — engine does not read this yet. Missing / other ⇒ <c>medium</c>.
    /// UI currently disables selecting <c>high</c>.
    /// </summary>
    public const string SelfReviewIntensity = "self_review_intensity";
}

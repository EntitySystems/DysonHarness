namespace DysonHarness;

/// <summary>Known <see cref="DysonAppSettingEntity.Key"/> values.</summary>
public static class DysonAppSettingKeys
{
    /// <summary>
    /// Guid string of the fallback chat model slug used after the session provider
    /// exhausts inference retries (or on immediate 401/403). Empty / missing ⇒ disabled
    /// (fail the turn after retries). Unlike other role pickers, empty does <em>not</em>
    /// mean “use the session model”.
    /// </summary>
    public const string FallbackChatModelSlugId = "fallback_chat_model_slug_id";

    /// <summary>
    /// Reasoning-effort override for the fallback chat slug.
    /// Empty / missing ⇒ the slug’s <c>DefaultReasoningEffort</c>.
    /// </summary>
    public const string FallbackChatReasoningEffort = "fallback_chat_reasoning_effort";

    /// <summary>
    /// Guid string of the model slug used for web-search/fetch summarization.
    /// Empty / missing ⇒ use the session model.
    /// </summary>
    public const string WebSearchSummarizerModelSlugId = "web_search_summarizer_model_slug_id";

    /// <summary>
    /// Reasoning-effort override for the web-search summarizer slug.
    /// Empty / missing ⇒ the slug’s <c>DefaultReasoningEffort</c>.
    /// </summary>
    public const string WebSearchSummarizerReasoningEffort = "web_search_summarizer_reasoning_effort";

    /// <summary>
    /// Guid string of the model slug used for turn context summarization (<c>SummarizeTurns</c>).
    /// Empty / missing ⇒ use the session model.
    /// </summary>
    public const string TurnSummarizerModelSlugId = "turn_summarizer_model_slug_id";

    /// <summary>
    /// Reasoning-effort override for the turn summarizer slug.
    /// Empty / missing ⇒ the slug’s <c>DefaultReasoningEffort</c>.
    /// </summary>
    public const string TurnSummarizerReasoningEffort = "turn_summarizer_reasoning_effort";

    /// <summary>
    /// Guid string of the default model slug for Explore subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Empty / missing ⇒ inherit the parent session model.
    /// </summary>
    public const string ExploreModelSlugId = "explore_model_slug_id";

    /// <summary>
    /// Reasoning-effort override for the Explore default slug.
    /// Empty / missing ⇒ the slug’s <c>DefaultReasoningEffort</c>.
    /// </summary>
    public const string ExploreReasoningEffort = "explore_reasoning_effort";

    /// <summary>
    /// Guid string of the default model slug for Drone subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Empty / missing ⇒ inherit the parent session model.
    /// </summary>
    public const string DroneModelSlugId = "drone_model_slug_id";

    /// <summary>
    /// Reasoning-effort override for the Drone default slug.
    /// Empty / missing ⇒ the slug’s <c>DefaultReasoningEffort</c>.
    /// </summary>
    public const string DroneReasoningEffort = "drone_reasoning_effort";

    /// <summary>
    /// Guid string of the default model slug for Security Review subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Empty / missing ⇒ inherit the parent session model.
    /// </summary>
    public const string SecurityReviewModelSlugId = "security_review_model_slug_id";

    /// <summary>
    /// Reasoning-effort override for the Security Review default slug.
    /// Empty / missing ⇒ the slug’s <c>DefaultReasoningEffort</c>.
    /// </summary>
    public const string SecurityReviewReasoningEffort = "security_review_reasoning_effort";

    /// <summary>
    /// Guid string of the default model slug for Bug Review subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Empty / missing ⇒ inherit the parent session model.
    /// </summary>
    public const string BugReviewModelSlugId = "bug_review_model_slug_id";

    /// <summary>
    /// Reasoning-effort override for the Bug Review default slug.
    /// Empty / missing ⇒ the slug’s <c>DefaultReasoningEffort</c>.
    /// </summary>
    public const string BugReviewReasoningEffort = "bug_review_reasoning_effort";

    /// <summary>
    /// Guid string of the dedicated direct-OpenAI model slug used by <c>GenerateImage</c>.
    /// Empty, invalid, or unsupported ⇒ image generation is disabled; it never falls back to the chat model.
    /// </summary>
    public const string ImageGenerationModelSlugId = "image_generation_model_slug_id";

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
    /// UI theme: <c>"dark"</c> / <c>"light"</c>. Source of truth for Settings → General.
    /// Missing ⇒ fall back to browser <c>localStorage</c> (<c>dyson-theme</c>), then dark.
    /// </summary>
    public const string UiTheme = "ui_theme";

    /// <summary>
    /// UI accent: <c>"blue"</c> / <c>"green"</c> / <c>"red"</c> / <c>"purple"</c> / <c>"amber"</c>.
    /// Missing ⇒ fall back to browser <c>localStorage</c> (<c>dyson-theme</c>), then blue.
    /// </summary>
    public const string UiAccent = "ui_accent";

    /// <summary>
    /// CalVer of an in-app update the user declined (e.g. <c>"2026.8.142"</c>).
    /// The updater prompts again only for a strictly newer release. Missing ⇒ nothing skipped.
    /// </summary>
    public const string UiUpdateSkippedVersion = "ui_update_skipped_version";

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
    /// Automatic code review after a root session finishes ReportSummary:
    /// <c>none</c> / <c>low</c> / <c>medium</c>. Persisted <c>high</c> is stale and
    /// non-actionable (UI shows it disabled). Missing ⇒ migrate from the obsolete
    /// <see cref="EndOfTaskAutoReview"/> / <see cref="SelfReviewIntensity"/> keys on
    /// first settings load or host resolve — see <see cref="DysonAutomaticCodeReviewSetting"/>.
    /// </summary>
    public const string AutomaticCodeReview = "automatic_code_review";

    /// <summary>
    /// Automatic code-review follow-up: <c>report_only</c> (default) or
    /// <c>automatically_fix</c>. Missing values migrate to <c>report_only</c>.
    /// </summary>
    public const string AutomaticCodeReviewAction = "automatic_code_review_action";

    /// <summary>
    /// Obsolete Boolean toggle (<c>"true"</c> / <c>"false"</c>). Readable for
    /// compatibility only — not the active engine behavior. Prefer
    /// <see cref="AutomaticCodeReview"/>.
    /// </summary>
    public const string EndOfTaskAutoReview = "end_of_task_auto_review";

    /// <summary>
    /// Obsolete intensity (<c>low</c> / <c>medium</c> / <c>high</c>). Readable for
    /// compatibility only — not the active engine behavior. Prefer
    /// <see cref="AutomaticCodeReview"/>.
    /// </summary>
    public const string SelfReviewIntensity = "self_review_intensity";

    /// <summary>
    /// JSON document for S3-compatible file storage credentials:
    /// <c>{"endpointUrl","accessKeyId","secretAccessKey"}</c>.
    /// Empty / missing ⇒ not configured. Empty/whitespace delete via <c>SetSettingAsync</c>.
    /// </summary>
    public const string FileStorageS3 = "file_storage_s3";
}

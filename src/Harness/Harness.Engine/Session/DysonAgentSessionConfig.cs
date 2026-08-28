namespace DysonHarness;

public class DysonAgentSessionConfig
{
    /// <summary>
    /// Local custom agent system prompts keyed by mode string.
    /// Used when agentMode is not a built-in mode name.
    /// </summary>
    public Dictionary<string, string> CustomAgents { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolved enabled plugin assets for this session. The host supplies this immutable snapshot
    /// when creating or restoring a session; consumers must not use process-wide plugin state.
    /// </summary>
    public DysonPluginContributionSet PluginContributions { get; init; } = new();

    /// <summary>
    /// Current presentation snapshot (light/dark + accent). The host may replace it between turns.
    /// Children share the parent config instance.
    /// </summary>
    public DysonUiThemeSnapshot UiTheme { get; set; } = DysonUiThemeSnapshot.Default;

    /// <summary>
    /// FullAccess runs tools directly. AutoReview routes calls through the in-process MCP proxy.
    /// No allowlist either way.
    /// </summary>
    public DysonMcpAccessMode McpAccessMode { get; set; } = DysonMcpAccessMode.FullAccess;

    /// <summary>
    /// Enabled shells for ShellExecute / StartLongRunningShell (MCP enum = <see cref="DysonConfiguredShellSpec.Name"/>).
    /// Empty ⇒ those tools (and all long-running shell tools) are omitted. Hosts load from
    /// <see cref="IDysonConfiguredShellRepository"/>; tests set explicitly.
    /// </summary>
    public IReadOnlyList<DysonConfiguredShellSpec> AvailableShells { get; set; } = [];

    /// <summary>
    /// Optional Brave Search API key for FreeSearch / FreeSearchAdvanced.
    /// Falls back to env <c>BRAVE_API_KEY</c> when unset.
    /// </summary>
    public string? BraveApiKey { get; set; }

    /// <summary>
    /// Optional fallback chat provider used after the session provider exhausts inference
    /// retries (or on immediate 401/403). Null ⇒ disabled (fail the turn after retries).
    /// Unlike other role providers, null does <em>not</em> mean “use the session provider”.
    /// Hydrated only for OpenAI-compatible slugs.
    /// </summary>
    public DysonAgentProvider? FallbackChatProvider { get; set; }

    /// <summary>
    /// Optional provider for web-search/fetch result summarization.
    /// Null ⇒ use the session <see cref="DysonAgentSession.Provider"/>.
    /// </summary>
    public DysonAgentProvider? SummarizerProvider { get; set; }

    /// <summary>
    /// Optional provider for turn context summarization (<c>SummarizeTurns</c>).
    /// Null ⇒ use the session <see cref="DysonAgentSession.Provider"/>.
    /// </summary>
    public DysonAgentProvider? TurnSummarizerProvider { get; set; }

    /// <summary>
    /// Optional default provider for Explore subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Null ⇒ inherit the parent session provider.
    /// </summary>
    public DysonAgentProvider? ExploreDefaultProvider { get; set; }

    /// <summary>
    /// Optional default provider for Drone subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Null ⇒ inherit the parent session provider.
    /// </summary>
    public DysonAgentProvider? DroneDefaultProvider { get; set; }

    /// <summary>
    /// Optional default provider for Security Review subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Null ⇒ inherit the parent session provider.
    /// </summary>
    public DysonAgentProvider? SecurityReviewDefaultProvider { get; set; }

    /// <summary>
    /// Optional default provider for Bug Review subagents when <c>StartSubagent.modelSlug</c> is omitted.
    /// Null ⇒ inherit the parent session provider.
    /// </summary>
    public DysonAgentProvider? BugReviewDefaultProvider { get; set; }

    /// <summary>
    /// Dedicated direct-OpenAI provider identity for image generation.
    /// Null means <c>GenerateImage</c> is unavailable; it never falls back to the session chat provider.
    /// </summary>
    public OpenAiCompatibleAgentProvider? ImageGenerationProvider { get; set; }

    /// <summary>
    /// Settings default provider for Explore / Drone / Security Review / Bug Review; other modes ⇒ null (inherit).
    /// </summary>
    public DysonAgentProvider? TryGetSubagentDefaultProvider(string? agentMode)
    {
        if (string.IsNullOrWhiteSpace(agentMode))
            return null;

        if (string.Equals(agentMode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
            return ExploreDefaultProvider;
        if (string.Equals(agentMode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
            return DroneDefaultProvider;
        if (string.Equals(agentMode, DysonAgentModes.SecurityReview, StringComparison.OrdinalIgnoreCase))
            return SecurityReviewDefaultProvider;
        if (string.Equals(agentMode, DysonAgentModes.BugReview, StringComparison.OrdinalIgnoreCase))
            return BugReviewDefaultProvider;

        return null;
    }

    /// <summary>
    /// Resolve-order helper when spawning a child: explicit <paramref name="modelSlug"/> wins (returns null —
    /// caller looks up the slug); otherwise returns the settings default for that mode when configured;
    /// otherwise null (caller inherits the parent provider).
    /// </summary>
    public DysonAgentProvider? TryGetSubagentDefaultWhenSlugOmitted(string? modelSlug, string? agentMode)
    {
        if (!string.IsNullOrWhiteSpace(modelSlug))
            return null;

        return TryGetSubagentDefaultProvider(agentMode);
    }

    /// <summary>
    /// Optional process-wide browser control (Windows CefSharp host).
    /// Null ⇒ browser MCP tools are omitted from the catalog.
    /// </summary>
    public IDysonBrowserControl? BrowserControl { get; set; }

    /// <summary>
    /// Optional workdir-scoped custom MCP host (<c>.dyson/mcp</c>).
    /// When set and <see cref="DysonCustomMcpHost.McpActive"/>, namespaced tools are merged into the catalog.
    /// </summary>
    public DysonCustomMcpHost? CustomMcpHost { get; set; }

    /// <summary>
    /// Optional managed plugin MCP host. Installation and enablement do not activate servers;
    /// the host exposes only checksum-bound explicitly granted tools.
    /// </summary>
    public DysonPluginMcpHost? PluginMcpHost { get; set; }

    /// <summary>Work-directory scope used to rebuild this session's effective plugin MCP catalog.</summary>
    public Guid? PluginMcpWorkDirectoryId { get; set; }

    /// <summary>
    /// Pre-resolved tool denylist for this session's current mode.
    /// Used when <see cref="ToolPolicy"/> is null (tests / hosts that resolve once).
    /// </summary>
    public IReadOnlySet<string>? DisabledTools { get; set; }

    /// <summary>
    /// Full policy document for re-resolve on mode switch and child spawn.
    /// When set, preferred over <see cref="DisabledTools"/> for catalog builds.
    /// </summary>
    public DysonToolPolicyDocument? ToolPolicy { get; set; }
}

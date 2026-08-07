namespace DysonHarness;

public class DysonAgentSessionConfig
{
    /// <summary>
    /// Local custom agent system prompts keyed by mode string.
    /// Used when agentMode is not a built-in mode name.
    /// </summary>
    public Dictionary<string, string> CustomAgents { get; } = new(StringComparer.Ordinal);

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
    /// Optional provider for web-search/fetch result summarization.
    /// Null ⇒ use the session <see cref="DysonAgentSession.Provider"/>.
    /// </summary>
    public DysonAgentProvider? SummarizerProvider { get; set; }

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

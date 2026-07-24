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
    /// Shells listed on the ShellExecute MCP enum for this session.
    /// Defaults from <see cref="DysonShell.AvailableForCurrentPlatform"/>.
    /// </summary>
    public IReadOnlyList<DysonShellType> AvailableShellTypes { get; set; } =
        DysonShell.AvailableForCurrentPlatform();

    /// <summary>
    /// Optional Brave Search API key for FreeSearch / FreeSearchAdvanced.
    /// Falls back to env <c>BRAVE_API_KEY</c> when unset.
    /// </summary>
    public string? BraveApiKey { get; set; }
}

namespace DysonHarness;

public enum DysonCustomMcpTransportKind
{
    Stdio = 0,
    HttpAutoDetect = 1,
    HttpStreamable = 2,
    HttpSse = 3,
}

/// <summary>Parsed + env-expanded custom MCP server config from <c>.dyson/mcp/{serverId}.json</c>.</summary>
public sealed class DysonCustomMcpServerConfig
{
    public required string ServerId { get; init; }

    public DysonCustomMcpTransportKind Transport { get; init; }

    public bool Disabled { get; init; }

    /// <summary>Raw JSON text as stored on disk (before expansion), for the settings editor.</summary>
    public string RawJson { get; init; } = "{}";

    // Stdio
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string? Cwd { get; init; }

    // HTTP
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

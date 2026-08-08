namespace DysonHarness;

/// <summary>
/// Runtime-ready MCP connection options shared by user-authored custom MCP and managed plugin MCP.
/// Callers are responsible for applying their source-specific validation and expansion rules first.
/// </summary>
public sealed record DysonMcpClientConnectionOptions
{
    public required string ServerId { get; init; }
    public required DysonCustomMcpTransportKind Transport { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string? Cwd { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool DisableAutoRedirect { get; init; }
}

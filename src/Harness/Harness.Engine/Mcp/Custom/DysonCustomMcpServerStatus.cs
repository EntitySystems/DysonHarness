namespace DysonHarness;

public enum DysonCustomMcpServerConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Disabled = 3,
    Error = 4,
}

/// <summary>UI / host status row for one custom MCP server.</summary>
public sealed class DysonCustomMcpServerStatus
{
    public required string ServerId { get; init; }
    public DysonCustomMcpTransportKind Transport { get; init; }
    public DysonCustomMcpServerConnectionState State { get; init; }
    public bool Disabled { get; init; }
    public int ToolCount { get; init; }
    public string? LastError { get; init; }
}

namespace DysonHarness;

/// <summary>
/// Minimal MCP-shaped tool metadata for catalog injection (execution later).
/// </summary>
public sealed class DysonMcpTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema object text for parameters (kept as string for now).</summary>
    public required string InputSchemaJson { get; init; }
}

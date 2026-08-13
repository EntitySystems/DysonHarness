namespace DysonHarness;

/// <summary>Structured, sandboxed HTML visualization payload attached to a tool result.</summary>
public sealed class DysonHtmlVisualization
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required string Html { get; init; }
    public required string Css { get; init; }
    public required string JavaScript { get; init; }
}

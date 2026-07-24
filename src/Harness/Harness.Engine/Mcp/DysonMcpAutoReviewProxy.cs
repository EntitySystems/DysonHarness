namespace DysonHarness;

/// <summary>
/// In-process auto-review path for MCP tool calls. No allowlist.
/// Later: every tool call is reviewed in-process; approve → execute; deny → return tool error to the model.
/// </summary>
public sealed class DysonMcpAutoReviewProxy
{
    public DysonMcpPipeline Pipeline { get; }

    public DysonMcpAutoReviewProxy(DysonMcpPipeline pipeline)
    {
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }
}

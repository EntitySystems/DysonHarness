namespace DysonHarness;

public sealed class DysonToolCallResult
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required int Stage { get; init; }
    public bool IsError { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

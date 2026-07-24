namespace DysonHarness;

public enum DysonToolCallStatus
{
    Queued = 0,
    Working = 1,
    Completed = 2,
    Failed = 3,
}

public sealed class DysonToolCall
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required int Stage { get; init; }
    public string ArgumentsJson { get; init; } = "{}";
}

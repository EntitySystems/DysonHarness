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

    /// <summary>
    /// Set by the provider clients when the model round was cut off (stream ended early or output-token
    /// limit) and this call's arguments never became valid JSON. The scheduler fails the call with this
    /// text instead of running it; <see cref="ArgumentsJson"/> is then just <c>{}</c>.
    /// </summary>
    public string? ArgumentsError { get; init; }
}

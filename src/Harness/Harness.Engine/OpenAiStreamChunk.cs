namespace DysonHarness;

/// <summary>Parsed Completions or Responses model reply (streaming final or non-streaming parse).</summary>
public sealed class OpenAiModelReply
{
    public string? Content { get; init; }
    public IReadOnlyList<DysonToolCall> ToolCalls { get; init; } = [];
    public string? ResponseId { get; init; }
    public string? UsageCacheHint { get; init; }
}

/// <summary>Incremental delta from a streaming Completions or Responses round.</summary>
public sealed class OpenAiStreamChunk
{
    public string? TextDelta { get; init; }
    public IReadOnlyList<OpenAiStreamToolCallDelta>? ToolCallDeltas { get; init; }
    public bool IsRoundComplete { get; init; }
    public OpenAiModelReply? CompletedReply { get; init; }
}

/// <summary>Partial tool-call fragment from a streaming chunk (Completions index or Responses item).</summary>
public sealed class OpenAiStreamToolCallDelta
{
    public int Index { get; init; }
    public string? CallId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsDelta { get; init; }
}

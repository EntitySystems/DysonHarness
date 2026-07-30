using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>Parsed Completions or Responses model reply (streaming final or non-streaming parse).</summary>
public sealed class OpenAiModelReply
{
    public string? Content { get; init; }
    /// <summary>Model reasoning / thinking text when the provider emits it (UI + persist only).</summary>
    public string? ReasoningContent { get; init; }
    public IReadOnlyList<DysonToolCall> ToolCalls { get; init; } = [];
    public string? ResponseId { get; init; }
    public string? UsageCacheHint { get; init; }

    /// <summary>Provider <c>prompt_tokens</c> / <c>input_tokens</c> when usage is present.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>
    /// Raw Responses <c>type:reasoning</c> output items (incl. <c>encrypted_content</c>) for
    /// stateless tool-loop replay. Separate from UI <see cref="ReasoningContent"/>.
    /// </summary>
    public IReadOnlyList<JsonObject> ReasoningOutputItems { get; init; } = [];
}

/// <summary>Incremental delta from a streaming Completions or Responses round.</summary>
public sealed class OpenAiStreamChunk
{
    public string? TextDelta { get; init; }
    /// <summary>Incremental reasoning / thinking text delta (Completions <c>reasoning_content</c> or Responses reasoning events).</summary>
    public string? ReasoningDelta { get; init; }
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

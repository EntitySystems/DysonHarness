namespace DysonHarness;

/// <summary>
/// Optional binary/image payload for provider multimodal parts (e.g. LoadBinary).
/// <see cref="FileName"/> is the original name including extension (e.g. <c>shot.png</c>).
/// Keep <see cref="DysonToolCallResult.Content"/> as a short ack — do not stuff base64 there.
/// </summary>
public sealed class DysonBinaryAttachment
{
    /// <summary>Original file name including extension (e.g. <c>dxcompiler.dll</c>).</summary>
    public required string FileName { get; init; }

    /// <summary>Extension including the leading dot (e.g. <c>.png</c>), or empty.</summary>
    public required string Extension { get; init; }

    public required string MimeType { get; init; }

    public required string Base64Data { get; init; }

    public bool IsImage =>
        MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed class DysonToolCallResult
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required int Stage { get; init; }
    public bool IsError { get; init; }
    public string Content { get; init; } = "";

    /// <summary>
    /// When set (LoadBinary / BrowserTakeScreenshot), transcript builders emit a follow-up
    /// multimodal user/input part with filename+extension metadata; not inlined into
    /// <see cref="Content"/>. Cleared after the turn finalizes (one-shot vision).
    /// </summary>
    public DysonBinaryAttachment? BinaryAttachment { get; init; }

    /// <summary>Copy without <see cref="BinaryAttachment"/> (ack <see cref="Content"/> kept).</summary>
    public DysonToolCallResult WithoutBinaryAttachment() =>
        BinaryAttachment is null
            ? this
            : new DysonToolCallResult
            {
                CallId = CallId,
                ToolName = ToolName,
                Stage = Stage,
                IsError = IsError,
                Content = Content,
                EndsCurrentTurn = EndsCurrentTurn,
                CompletedAt = CompletedAt,
            };

    /// <summary>
    /// When true (and not <see cref="IsError"/>), the tool loop soft-closes the calling turn
    /// after the staged round — no further model rounds on that turn.
    /// </summary>
    public bool EndsCurrentTurn { get; init; }

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

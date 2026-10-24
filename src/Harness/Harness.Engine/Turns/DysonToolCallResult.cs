namespace DysonHarness;

/// <summary>
/// Optional binary/image payload for provider multimodal parts (e.g. LoadBinary).
/// <see cref="FileName"/> is the original name including extension (e.g. <c>shot.png</c>).
/// Keep <see cref="DysonToolCallResult.Content"/> as a short ack — do not stuff base64 there.
/// Human-readable name goes in a text / input_text label — never on Completions image_url
/// or Responses input_image wire parts.
/// </summary>
public sealed class DysonBinaryAttachment
{
    /// <summary>Original file name including extension (e.g. <c>dxcompiler.dll</c>).</summary>
    public required string FileName { get; init; }

    /// <summary>Extension including the leading dot (e.g. <c>.png</c>), or empty.</summary>
    public required string Extension { get; init; }

    public required string MimeType { get; init; }

    public required string Base64Data { get; init; }

    /// <summary>
    /// OpenAI Files API id after upload (Responses only). Set by
    /// <c>OpenAiFilesClient.EnsureBinaryFileIdsAsync</c>; cleared with the one-shot attachment.
    /// </summary>
    public string? FileId { get; set; }

    /// <summary>
    /// Optional HTML/DOM reference for browser snips.
    /// TODO: future snip will resolve elements intersecting the selection; empty today.
    /// Not sent on provider wire image parts.
    /// </summary>
    public string? HtmlRef { get; init; }

    /// <summary>
    /// Stable presigned HTTPS GET URL for vision wire parts. When set, transcript builders
    /// prefer this over a data-URL / Files upload.
    /// </summary>
    public string? RemoteUrl { get; set; }

    /// <summary>S3 object key for the uploaded bytes (prefix + guid + file name).</summary>
    public string? ObjectKey { get; set; }

    /// <summary>UTC expiry of <see cref="RemoteUrl"/> (presigned GET lifetime).</summary>
    public DateTime? RemoteUrlExpiresUtc { get; set; }

    /// <summary>
    /// True after the first in-flight transcript emission. Later <see cref="RemoteUrl"/> /
    /// <see cref="FileId"/> writes must not change that hop's replay.
    /// </summary>
    /// <remarks>
    /// ponytail: the snapshot lives on this instance only. A new process resolves once,
    /// then freezes again; persisting the snapshot would keep an expired URL forever.
    /// </remarks>
    internal bool InFlightWireCaptured { get; private set; }

    /// <summary>Trimmed HTTPS URL from the first in-flight emission, or null when that emission had none.</summary>
    internal string? CapturedRemoteUrl { get; private set; }

    /// <summary>Files id from the first in-flight emission, or null when that emission had none.</summary>
    internal string? CapturedFileId { get; private set; }

    public bool IsImage =>
        MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Locks the URL and file id that the next replay must use. A second call is a no-op.
    /// </summary>
    internal void CaptureInFlightWire()
    {
        if (InFlightWireCaptured)
            return;

        CapturedRemoteUrl = string.IsNullOrWhiteSpace(RemoteUrl) ? null : RemoteUrl.Trim();
        CapturedFileId = string.IsNullOrEmpty(FileId) ? null : FileId;
        InFlightWireCaptured = true;
    }

    /// <summary>
    /// Wire fields for one emission. <paramref name="capture"/> freezes them the first time
    /// this attachment is sent on an in-flight hop.
    /// </summary>
    internal (string? RemoteUrl, string? FileId) GetReplayWire(bool capture)
    {
        if (capture)
            CaptureInFlightWire();

        if (InFlightWireCaptured)
            return (CapturedRemoteUrl, CapturedFileId);

        return (
            string.IsNullOrWhiteSpace(RemoteUrl) ? null : RemoteUrl.Trim(),
            string.IsNullOrEmpty(FileId) ? null : FileId);
    }
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
    /// multimodal user/input part; filename stays in a text label (and on non-image file parts).
    /// Not inlined into <see cref="Content"/>. After the turn finalizes, image attachments
    /// with <see cref="DysonBinaryAttachment.RemoteUrl"/> are kept slim (no local bytes);
    /// otherwise the attachment is cleared (legacy one-shot vision).
    /// </summary>
    public DysonBinaryAttachment? BinaryAttachment { get; init; }

    /// <summary>
    /// Structured visualization payload for UI rendering. Kept out of provider tool transcripts;
    /// <see cref="Content"/> remains the small model-facing acknowledgement.
    /// </summary>
    public DysonHtmlVisualization? HtmlVisualization { get; init; }

    /// <summary>
    /// Durable PNG metadata emitted by <c>GenerateImage</c>. Image bytes stay at the validated
    /// workspace paths; this list contains neither base64 data nor transient preview identifiers.
    /// </summary>
    public IReadOnlyList<DysonGeneratedImageArtifact> GeneratedImageArtifacts { get; init; } = [];

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
                HtmlVisualization = HtmlVisualization,
                GeneratedImageArtifacts = GeneratedImageArtifacts,
                EndsCurrentTurn = EndsCurrentTurn,
                CompletedAt = CompletedAt,
            };

    /// <summary>
    /// Copy with a slim <see cref="BinaryAttachment"/>: metadata + RemoteUrl fields kept,
    /// <see cref="DysonBinaryAttachment.Base64Data"/> emptied so SQLite stays small.
    /// </summary>
    public DysonToolCallResult WithoutLocalBytes()
    {
        if (BinaryAttachment is null)
            return this;

        var source = BinaryAttachment;
        var wire = source.GetReplayWire(capture: false);
        var slim = new DysonBinaryAttachment
        {
            FileName = source.FileName,
            Extension = source.Extension,
            MimeType = source.MimeType,
            Base64Data = "",
            FileId = wire.FileId,
            HtmlRef = source.HtmlRef,
            RemoteUrl = wire.RemoteUrl,
            ObjectKey = source.ObjectKey,
            RemoteUrlExpiresUtc = source.RemoteUrlExpiresUtc,
        };
        if (source.InFlightWireCaptured)
            slim.CaptureInFlightWire();

        return new DysonToolCallResult
        {
            CallId = CallId,
            ToolName = ToolName,
            Stage = Stage,
            IsError = IsError,
            Content = Content,
            BinaryAttachment = slim,
            HtmlVisualization = HtmlVisualization,
            GeneratedImageArtifacts = GeneratedImageArtifacts,
            EndsCurrentTurn = EndsCurrentTurn,
            CompletedAt = CompletedAt,
        };
    }

    /// <summary>
    /// Persist image attachments that have a RemoteUrl without JPEG bytes;
    /// otherwise drop the attachment (legacy one-shot vision).
    /// </summary>
    public DysonToolCallResult ForPersistence()
    {
        if (BinaryAttachment is { IsImage: true } attachment
            && !string.IsNullOrWhiteSpace(attachment.GetReplayWire(capture: false).RemoteUrl))
        {
            return WithoutLocalBytes();
        }

        return WithoutBinaryAttachment();
    }

    /// <summary>
    /// When true (and not <see cref="IsError"/>), the tool loop soft-closes the calling turn
    /// after the staged round — no further model rounds on that turn.
    /// </summary>
    public bool EndsCurrentTurn { get; init; }

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

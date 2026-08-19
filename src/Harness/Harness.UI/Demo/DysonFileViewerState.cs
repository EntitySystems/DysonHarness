using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Host state for the chat-preserving file viewer overlay.</summary>
public sealed class DysonFileViewerState
{
    public required string RelativePath { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required bool IsMarkdown { get; init; }

    /// <summary>When true, <see cref="PdfPreviewUrl"/> is the iframe src for a browser PDF view.</summary>
    public bool IsPdf { get; init; }

    /// <summary>Relative URL under <see cref="DysonFilePreviewStore.RoutePrefix"/>, or null when not a PDF preview.</summary>
    public string? PdfPreviewUrl { get; init; }

    /// <summary>Preview store token to revoke on close; null when not a PDF preview.</summary>
    public string? PdfPreviewId { get; init; }

    /// <summary>When true, <see cref="ImagePreviewUrl"/> is the <c>img</c> src for a browser image view.</summary>
    public bool IsImage { get; init; }

    /// <summary>Relative URL under <see cref="DysonFilePreviewStore.RoutePrefix"/>, or null when not an image preview.</summary>
    public string? ImagePreviewUrl { get; init; }

    /// <summary>Preview store token to revoke on close; null when not an image preview.</summary>
    public string? ImagePreviewId { get; init; }

    public string? AbsolutePath { get; init; }
    public string? Error { get; init; }

    /// <summary>Ordered footer CTAs (stable button order). Empty when none.</summary>
    public IReadOnlyList<DysonFileViewerAction> Actions { get; init; } = [];

    /// <summary>
    /// Git hunk annotations for a workspace text file. Empty when Git metadata is
    /// unavailable or the viewer is not a workspace text file.
    /// </summary>
    public IReadOnlyList<DysonGitDiffAnnotation> GitDiffAnnotations { get; init; } = [];

    public static bool IsPdfPath(string path) =>
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when bytes look like a PDF (%PDF magic), used when extension is absent.</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> head) =>
        head.Length >= 5
        && head[0] == (byte)'%'
        && head[1] == (byte)'P'
        && head[2] == (byte)'D'
        && head[3] == (byte)'F'
        && head[4] == (byte)'-';
}

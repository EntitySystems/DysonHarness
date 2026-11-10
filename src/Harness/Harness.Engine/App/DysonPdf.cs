using System.Globalization;

using PDFtoImage;
using PDFtoImage.Exceptions;

namespace DysonHarness;

/// <summary>
/// PDF detection and PDF→PNG page rendering (PDFtoImage / PDFium) for the ConvertImage MCP tool.
/// LoadBinary and subagent contextFiles reject PDFs and point at ConvertImage instead.
/// </summary>
internal static class DysonPdf
{
    public const int DefaultDpi = 150;
    public const int MinDpi = 36;
    public const int MaxDpi = 300;
    public const int MaxPagesPerCall = 20;
    public const int MaxEdgePx = 4096;

    /// <summary>Bytes needed for the <c>%PDF-</c> magic sniff.</summary>
    public const int HeadBytes = 5;

    public sealed record RenderedPage(int Page, byte[] Png, int Width, int Height);

    /// <summary><c>.pdf</c> extension (any case) or a head that starts with <c>%PDF-</c>.</summary>
    public static bool IsPdf(string? path, ReadOnlySpan<byte> head) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
        || head.StartsWith("%PDF-"u8);

    /// <summary>
    /// 1-based page spec: <c>"1"</c>, <c>"1-3"</c>, <c>"2,5"</c>, <c>"1-3,7"</c>, <c>"all"</c>;
    /// null/empty → page 1. De-duplicated and sorted; at most <see cref="MaxPagesPerCall"/>.
    /// </summary>
    public static Result<IReadOnlyList<int>, string> ParsePages(string? spec, int pageCount)
    {
        if (pageCount < 1)
            return Result<IReadOnlyList<int>, string>.AsError("PDF has no pages.");

        var pages = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(spec))
        {
            pages.Add(1);
        }
        else if (string.Equals(spec.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            for (var p = 1; p <= pageCount; p++)
                pages.Add(p);
        }
        else
        {
            foreach (var part in spec.Split(',', StringSplitOptions.TrimEntries))
            {
                var dash = part.IndexOf('-');
                var fromText = dash < 0 ? part : part[..dash];
                var toText = dash < 0 ? part : part[(dash + 1)..];
                if (!int.TryParse(fromText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                    || !int.TryParse(toText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                    || from < 1 || to < from)
                {
                    return Result<IReadOnlyList<int>, string>.AsError(
                        $"invalid pages '{spec}'. Use 1-based pages like '1', '1-3', '2,5' or 'all'.");
                }

                if (to > pageCount)
                {
                    return Result<IReadOnlyList<int>, string>.AsError(
                        $"page {to} out of range (pageCount {pageCount}).");
                }

                if (to - from + 1 > MaxPagesPerCall)
                    return TooMany();

                for (var p = from; p <= to; p++)
                    pages.Add(p);
            }
        }

        return pages.Count > MaxPagesPerCall
            ? TooMany()
            : Result<IReadOnlyList<int>, string>.AsValue(pages.ToList());

        static Result<IReadOnlyList<int>, string> TooMany() =>
            Result<IReadOnlyList<int>, string>.AsError(
                $"at most {MaxPagesPerCall} pages per call; split the range.");
    }

    /// <summary>
    /// Renders the requested pages to PNG at <paramref name="dpi"/> (lowered per page so the
    /// longest edge stays ≤ <see cref="MaxEdgePx"/>). CPU-bound: call from a thread-pool hop.
    /// </summary>
    public static Result<IReadOnlyList<RenderedPage>, string> RenderPages(byte[] pdf, string? pages, int dpi)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (dpi is < MinDpi or > MaxDpi)
            return Result<IReadOnlyList<RenderedPage>, string>.AsError($"dpi must be {MinDpi}–{MaxDpi} (got {dpi}).");

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return Result<IReadOnlyList<RenderedPage>, string>.AsError("PDF rendering is not supported on this OS.");

        try
        {
            var pageCount = Conversion.GetPageCount(pdf);
            var parsed = ParsePages(pages, pageCount);
            if (parsed.IsError)
                return Result<IReadOnlyList<RenderedPage>, string>.AsError(parsed.Error);

            var sizes = Conversion.GetPageSizes(pdf).ToList();
            var rendered = new List<RenderedPage>(parsed.Value.Count);
            foreach (var page in parsed.Value)
            {
                var size = sizes[page - 1];
                var longestPt = Math.Max(size.Width, size.Height);
                var effDpi = longestPt * dpi / 72f > MaxEdgePx
                    ? Math.Max(1, (int)Math.Floor(MaxEdgePx * 72f / longestPt))
                    : dpi;

                using var ms = new MemoryStream();
                Conversion.SavePng(ms, pdf, page - 1, options: new RenderOptions(Dpi: effDpi, WithAnnotations: true));
                var png = ms.ToArray();
                var (width, height) = PngSize(png);
                rendered.Add(new RenderedPage(page, png, width, height));
            }

            return Result<IReadOnlyList<RenderedPage>, string>.AsValue(rendered);
        }
        catch (Exception ex) when (ex is PdfPasswordProtectedException or PdfUnsupportedSecuritySchemeException)
        {
            return Result<IReadOnlyList<RenderedPage>, string>.AsError("PDF is password-protected or encrypted; decrypt it first.");
        }
        catch (Exception ex) when (ex is PdfInvalidFormatException or PdfCannotOpenFileException)
        {
            return Result<IReadOnlyList<RenderedPage>, string>.AsError("file is not a valid PDF or is corrupted.");
        }
        catch (PdfPageNotFoundException ex)
        {
            return Result<IReadOnlyList<RenderedPage>, string>.AsError($"page out of range: {ex.Message}");
        }
        catch (PdfException ex)
        {
            return Result<IReadOnlyList<RenderedPage>, string>.AsError($"PDF render failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Result<IReadOnlyList<RenderedPage>, string>.AsError($"PDF renderer native library unavailable: {ex.Message}");
        }
    }

    /// <summary>IHDR width/height (big-endian at bytes 16–23 of any PNG).</summary>
    private static (int Width, int Height) PngSize(byte[] png) =>
        png.Length < 24
            ? (0, 0)
            : (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
               System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
}

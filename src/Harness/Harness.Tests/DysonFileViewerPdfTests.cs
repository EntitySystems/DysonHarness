using System.Text;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>PDF path / magic helpers and ephemeral preview store (Xunit).</summary>
public class DysonFileViewerPdfTests
{
    [Fact]
    public void Run()
    {
        if (!DysonFileViewerState.IsPdfPath("docs/a.PDF")
            || !DysonFileViewerState.IsPdfPath("x.pdf")
            || DysonFileViewerState.IsPdfPath("x.md")
            || DysonFileViewerState.IsPdfPath("pdf.txt"))
        {
            throw new InvalidOperationException("IsPdfPath extension mismatch.");
        }

        var magic = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        if (!DysonFileViewerState.LooksLikePdf(magic)
            || DysonFileViewerState.LooksLikePdf("%PDF"u8)
            || DysonFileViewerState.LooksLikePdf("hello"u8))
        {
            throw new InvalidOperationException("LooksLikePdf magic mismatch.");
        }

        var store = new DysonFilePreviewStore();
        var id = store.Put([1, 2, 3], "application/pdf");
        if (!store.TryGet(id, out var entry)
            || entry.ContentType != "application/pdf"
            || entry.Bytes is not [1, 2, 3]
            || DysonFilePreviewStore.UrlFor(id) != $"{DysonFilePreviewStore.RoutePrefix}/{id}")
        {
            throw new InvalidOperationException("Preview store put/get/url mismatch.");
        }

        store.Remove(id);
        if (store.TryGet(id, out _))
            throw new InvalidOperationException("Preview store must remove by id.");
    }
}

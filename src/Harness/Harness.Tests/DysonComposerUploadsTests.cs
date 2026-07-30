using DysonHarness;

namespace Harness.Tests;

/// <summary>ponytail: composer non-image uploads land under .dyson/composer-uploads with unique names.</summary>
public class DysonComposerUploadsTests
{
    [Fact]
    public void Run()
    {
        AssertWriteUnderComposerUploads();
        AssertDedupeSuffix();
        AssertRejectsEmptyAndOversized();
        AssertLooksLikeImage();
    }

    private static void AssertLooksLikeImage()
    {
        if (!DysonComposerUploads.LooksLikeImage("image/png", "x.bin"))
            throw new InvalidOperationException("image/* MIME must win.");
        if (DysonComposerUploads.LooksLikeImage("application/pdf", "shot.png"))
            throw new InvalidOperationException("Non-image MIME must win over extension.");
        if (!DysonComposerUploads.LooksLikeImage("", "shot.PNG"))
            throw new InvalidOperationException("Empty MIME + .png must look like image.");
        if (!DysonComposerUploads.LooksLikeImage(null, "photo.jpeg"))
            throw new InvalidOperationException("Null MIME + .jpeg must look like image.");
        if (DysonComposerUploads.LooksLikeImage("", "notes.pdf"))
            throw new InvalidOperationException(".pdf must not look like image.");

        if (DysonComposerUploads.ImageContentTypeFromFileName("shot.PNG") != "image/png"
            || DysonComposerUploads.ImageContentTypeFromFileName("photo.jpeg") != "image/jpeg"
            || DysonComposerUploads.ImageContentTypeFromFileName("a.webp") != "image/webp"
            || DysonComposerUploads.ImageContentTypeFromFileName("notes.pdf") != "application/octet-stream")
        {
            throw new InvalidOperationException("ImageContentTypeFromFileName mismatch.");
        }
    }

    private static void AssertWriteUnderComposerUploads()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = CreateFs(root);
            var bytes = "hello notes"u8.ToArray();
            var written = DysonComposerUploads.Write(fs, "My Notes.txt", bytes);
            if (written.IsError)
                throw new InvalidOperationException(written.Error);

            var relative = written.Value.Replace('\\', '/');
            if (relative != ".dyson/composer-uploads/My Notes.txt")
                throw new InvalidOperationException($"Unexpected relative path: {relative}");

            var onDisk = Path.Combine(root, ".dyson", "composer-uploads", "My Notes.txt");
            if (!File.Exists(onDisk) || File.ReadAllText(onDisk) != "hello notes")
                throw new InvalidOperationException("Upload bytes were not written to disk.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertDedupeSuffix()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = CreateFs(root);
            var first = DysonComposerUploads.Write(fs, "report.pdf", [1, 2, 3]);
            var second = DysonComposerUploads.Write(fs, "report.pdf", [4, 5, 6]);
            if (first.IsError || second.IsError)
                throw new InvalidOperationException(first.IsError ? first.Error : second.Error);

            if (first.Value.Replace('\\', '/') != ".dyson/composer-uploads/report.pdf")
                throw new InvalidOperationException("First write must keep original name.");
            if (second.Value.Replace('\\', '/') != ".dyson/composer-uploads/report-1.pdf")
                throw new InvalidOperationException($"Expected report-1.pdf, got {second.Value}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertRejectsEmptyAndOversized()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = CreateFs(root);
            var empty = DysonComposerUploads.Write(fs, "empty.bin", []);
            if (!empty.IsError)
                throw new InvalidOperationException("Empty file must fail.");

            var huge = new byte[DysonComposerUploads.MaxRawBytes + 1];
            var oversized = DysonComposerUploads.Write(fs, "huge.bin", huge);
            if (!oversized.IsError)
                throw new InvalidOperationException("Oversized file must fail.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static IDysonWorkspaceFileSystem CreateFs(string root)
    {
        var created = DysonWorkspaceFileSystems.CreateLocalAsync(root).GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);
        return created.Value;
    }
}

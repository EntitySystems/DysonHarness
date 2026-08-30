using DysonHarness;
using ImageMagick;

namespace Harness.Tests;

/// <summary>
/// ponytail: composer uploads land under .dyson/composer-uploads; images dual-write JPEG;
/// ClearAll empties the folder; path helpers gate the Files-rail clear menu.
/// </summary>
public class DysonComposerUploadsTests
{
    [Fact]
    public void Run()
    {
        AssertWriteUnderComposerUploads();
        AssertDedupeSuffix();
        AssertRejectsEmptyAndOversized();
        AssertLooksLikeImage();
        AssertComposerUploadsPathHelpers();
        AssertImageDualWriteUnderComposerUploads();
        AssertClearAllEmptiesDirectory();
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
        if (DysonComposerUploads.LooksLikeImage(null, null)
            || DysonComposerUploads.LooksLikeImage("", "no-extension"))
            throw new InvalidOperationException("Null/empty extension must not look like image.");

        if (DysonComposerUploads.ImageContentTypeFromFileName("shot.PNG") != "image/png"
            || DysonComposerUploads.ImageContentTypeFromFileName("photo.jpeg") != "image/jpeg"
            || DysonComposerUploads.ImageContentTypeFromFileName("a.webp") != "image/webp"
            || DysonComposerUploads.ImageContentTypeFromFileName("notes.pdf") != "application/octet-stream"
            || DysonComposerUploads.ImageContentTypeFromFileName(null) != "application/octet-stream"
            || DysonComposerUploads.ImageContentTypeFromFileName("no-extension") != "application/octet-stream")
        {
            throw new InvalidOperationException("ImageContentTypeFromFileName mismatch.");
        }
    }

    private static void AssertComposerUploadsPathHelpers()
    {
        if (!DysonComposerUploads.IsComposerUploadsDirectory(".dyson/composer-uploads")
            || !DysonComposerUploads.IsComposerUploadsDirectory(@".dyson\composer-uploads\")
            || DysonComposerUploads.IsComposerUploadsDirectory(".dyson/composer-uploads/shot.jpg")
            || DysonComposerUploads.IsComposerUploadsDirectory(".dyson")
            || DysonComposerUploads.IsComposerUploadsDirectory(""))
        {
            throw new InvalidOperationException("IsComposerUploadsDirectory mismatch.");
        }

        if (!DysonComposerUploads.IsUnderComposerUploads(".dyson/composer-uploads")
            || !DysonComposerUploads.IsUnderComposerUploads(".dyson/composer-uploads/a.jpg")
            || DysonComposerUploads.IsUnderComposerUploads(".dyson/other/a.jpg")
            || DysonComposerUploads.IsUnderComposerUploads(".dyson/composer-uploads-old/x"))
        {
            throw new InvalidOperationException("IsUnderComposerUploads mismatch.");
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

    private static void AssertImageDualWriteUnderComposerUploads()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = CreateFs(root);
            var pngBytes = TinyPng();
            var created = DysonUserImageFactory.CreateFromBytes("paste-shot.png", pngBytes);
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var jpegBytes = Convert.FromBase64String(created.Value.Base64Data);
            var written = DysonComposerUploads.Write(fs, created.Value.FileName, jpegBytes);
            if (written.IsError)
                throw new InvalidOperationException(written.Error);

            var relative = written.Value.Replace('\\', '/');
            if (!relative.StartsWith(".dyson/composer-uploads/", StringComparison.Ordinal)
                || !relative.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected uploads JPEG path, got {relative}");
            }

            var onDisk = Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(onDisk) || new FileInfo(onDisk).Length == 0)
                throw new InvalidOperationException("Compressed image was not written under composer-uploads.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertClearAllEmptiesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = CreateFs(root);
            var a = DysonComposerUploads.Write(fs, "a.txt", "one"u8.ToArray());
            var b = DysonComposerUploads.Write(fs, "b.txt", "two"u8.ToArray());
            if (a.IsError || b.IsError)
                throw new InvalidOperationException(a.IsError ? a.Error : b.Error);

            var cleared = DysonComposerUploads.ClearAll(fs);
            if (cleared.IsError)
                throw new InvalidOperationException(cleared.Error);
            if (cleared.Value != 2)
                throw new InvalidOperationException($"Expected 2 deleted, got {cleared.Value}");

            var dir = Path.Combine(root, ".dyson", "composer-uploads");
            if (!Directory.Exists(dir))
                throw new InvalidOperationException("ClearAll must keep the uploads folder.");
            if (Directory.GetFileSystemEntries(dir).Length != 0)
                throw new InvalidOperationException("ClearAll must empty the uploads folder.");

            var again = DysonComposerUploads.ClearAll(fs);
            if (again.IsError || again.Value != 0)
                throw new InvalidOperationException("Second ClearAll on empty folder must return 0.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static byte[] TinyPng()
    {
        using var image = new MagickImage(MagickColors.Red, 8, 8);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static IDysonWorkspaceFileSystem CreateFs(string root)
    {
        var created = DysonWorkspaceFileSystems.CreateLocalAsync(root).GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);
        return created.Value;
    }
}

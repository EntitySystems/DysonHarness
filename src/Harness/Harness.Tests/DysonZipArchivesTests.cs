using System.IO.Compression;
using System.Text;
using DysonHarness;

namespace Harness.Tests;

public class DysonZipArchivesTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), $"dyson-zip-test-{Guid.NewGuid():N}");

    private static void WriteZip(string path, params (string Name, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var output = entry.Open();
            output.Write(data, 0, data.Length);
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static void DeleteQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task ReadEntries_and_ListFolder_walk_folders()
    {
        var dir = NewTempDir();
        try
        {
            var zipPath = Path.Combine(dir, "pack.zip");
            WriteZip(zipPath,
                ("readme.txt", Utf8("hello")),
                ("src/a.cs", Utf8("class A {}")),
                ("src/lib/b.cs", Utf8("class B {}")),
                ("docs/", []));

            var read = await DysonZipArchives.ReadEntriesAsync(zipPath);
            Assert.True(read.IsSuccess, read.IsError ? read.Error : null);

            var root = DysonZipArchives.ListFolder(read.Value, "");
            Assert.Equal(new[] { "docs", "src", "readme.txt" }, root.Select(e => e.Name));
            Assert.Equal(new[] { true, true, false }, root.Select(e => e.IsDirectory));

            var src = DysonZipArchives.ListFolder(read.Value, "src");
            Assert.Equal(new[] { "lib", "a.cs" }, src.Select(e => e.Name));
            Assert.Equal(new[] { "src/lib", "src/a.cs" }, src.Select(e => e.FullName));
            Assert.Equal((long)"class A {}".Length, src[1].Length);

            Assert.Equal("src", DysonZipArchives.ParentFolder("src/lib"));
            Assert.Equal("", DysonZipArchives.ParentFolder("src"));
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public async Task Extract_reuses_cache_and_invalidates_on_archive_change()
    {
        var dir = NewTempDir();
        try
        {
            var zipPath = Path.Combine(dir, "pack.zip");
            var cacheRoot = Path.Combine(dir, "cache");
            WriteZip(zipPath, ("readme.txt", Utf8("hello")));

            var first = await DysonZipArchives.ExtractEntryAsync(zipPath, "readme.txt", cacheRoot);
            Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
            Assert.Equal("hello", await File.ReadAllTextAsync(first.Value.AbsolutePath));
            Assert.True(first.Value.OpenInViewer);
            Assert.Equal("readme.txt", first.Value.RelativePath);

            await File.WriteAllTextAsync(first.Value.AbsolutePath, "MARKR"); // same length as "hello"
            var hit = await DysonZipArchives.ExtractEntryAsync(zipPath, "readme.txt", cacheRoot);
            Assert.True(hit.IsSuccess, hit.IsError ? hit.Error : null);
            Assert.Equal(first.Value.AbsolutePath, hit.Value.AbsolutePath);
            Assert.Equal("MARKR", await File.ReadAllTextAsync(hit.Value.AbsolutePath));

            WriteZip(zipPath, ("readme.txt", Utf8("changed content")));
            File.SetLastWriteTimeUtc(zipPath, File.GetLastWriteTimeUtc(zipPath).AddMinutes(1));
            var miss = await DysonZipArchives.ExtractEntryAsync(zipPath, "readme.txt", cacheRoot);
            Assert.True(miss.IsSuccess, miss.IsError ? miss.Error : null);
            Assert.NotEqual(first.Value.AbsolutePath, miss.Value.AbsolutePath);
            Assert.Equal("changed content", await File.ReadAllTextAsync(miss.Value.AbsolutePath));
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public async Task Extract_rejects_zip_slip_entries()
    {
        var dir = NewTempDir();
        try
        {
            var zipPath = Path.Combine(dir, "evil.zip");
            var cacheRoot = Path.Combine(dir, "cache");
            string[] names = ["../evil.txt", "/abs.txt", "a/../../x.txt", "C:/x.txt"];
            WriteZip(zipPath, [.. names.Select(n => (Name: n, Data: Utf8("pwn"))), ("ok.txt", Utf8("fine"))]);

            foreach (var name in names)
            {
                var result = await DysonZipArchives.ExtractEntryAsync(zipPath, name, cacheRoot);
                Assert.True(result.IsError, $"expected error for '{name}'");
            }

            Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "x.txt")));
            Assert.False(File.Exists(Path.Combine(cacheRoot, "evil.txt")));
            var outside = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.Equals(zipPath, StringComparison.Ordinal)
                    && !f.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.Empty(outside);

            var read = await DysonZipArchives.ReadEntriesAsync(zipPath);
            Assert.True(read.IsSuccess, read.IsError ? read.Error : null);
            Assert.Equal(new[] { "ok.txt" }, read.Value.Select(e => e.FullName));
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public async Task Extract_enforces_size_guard_and_flags_binary()
    {
        var dir = NewTempDir();
        try
        {
            var zipPath = Path.Combine(dir, "pack.zip");
            var cacheRoot = Path.Combine(dir, "cache");
            WriteZip(zipPath,
                ("big.txt", Utf8("0123456789")),
                ("blob.bin", [1, 2, 0, 3]));

            var big = await DysonZipArchives.ExtractEntryAsync(zipPath, "big.txt", cacheRoot, maxEntryBytes: 4);
            Assert.True(big.IsError);

            var blob = await DysonZipArchives.ExtractEntryAsync(zipPath, "blob.bin", cacheRoot);
            Assert.True(blob.IsSuccess, blob.IsError ? blob.Error : null);
            Assert.False(blob.Value.OpenInViewer);
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public async Task ReadEntries_returns_error_for_corrupt_zip()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var badPath = Path.Combine(dir, "bad.zip");
            await File.WriteAllTextAsync(badPath, "this is definitely not a zip archive, just some text bytes");

            var read = await DysonZipArchives.ReadEntriesAsync(badPath);
            Assert.True(read.IsError);
            Assert.StartsWith("Not a valid ZIP archive", read.Error);
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }
}

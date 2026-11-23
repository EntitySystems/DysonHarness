using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Image.path: exactly-one-of rule, height bounds, content types, open_file path rules, card data and card-text failures.</summary>
public class DysonA2uiImageTests
{
    private static string Surface(string props) =>
        DysonA2uiValidatorTests.Create("{\"id\":\"root\",\"component\":\"Image\"," + props + "}");

    [Fact]
    public void Validator_requires_exactly_one_of_url_or_path_and_bounds_height()
    {
        Assert.True(DysonA2uiValidator.Parse(Surface("\"url\":\"https://example.com/a.png\"")).IsSuccess);
        var ok = DysonA2uiValidator.Parse(Surface("\"path\":\"img/a.png\",\"height\":320,\"alt\":\"A\""));
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error.ToWireJson() : null);

        foreach (var props in new[] { "\"url\":\"https://example.com/a.png\",\"path\":\"a.png\"", "\"alt\":\"none\"" })
        {
            var bad = DysonA2uiValidator.Parse(Surface(props));
            Assert.True(bad.IsError, props);
            Assert.Contains("exactly one of url or path", bad.Error.Message, StringComparison.Ordinal);
        }

        var blank = DysonA2uiValidator.Parse(Surface("\"path\":\" \""));
        Assert.True(blank.IsError);
        Assert.EndsWith("/path", blank.Error.Path);

        foreach (var height in new[] { 100, 900 })
        {
            var bad = DysonA2uiValidator.Parse(Surface("\"path\":\"a.png\",\"height\":" + height));
            Assert.True(bad.IsError, height.ToString());
            Assert.EndsWith("/height", bad.Error.Path);
        }

        Assert.True(DysonA2uiValidator.Parse(Surface("\"url\":\"data:image/png;base64,AAAA\"")).IsError);
    }

    [Fact]
    public void PathPointers_lists_Image_paths_and_skips_url_only_images()
    {
        var both = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create(
            "{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"a\",\"b\"]},"
            + "{\"id\":\"a\",\"component\":\"Image\",\"url\":\"https://example.com/a.png\"},"
            + "{\"id\":\"b\",\"component\":\"Image\",\"path\":\"img/b.png\"}"));
        Assert.True(both.IsSuccess, both.IsError ? both.Error.ToWireJson() : null);
        var (pointer, path) = Assert.Single(DysonA2uiLocalVideo.PathPointers(both.Value, DysonA2uiImage.ComponentName));
        Assert.Equal("/messages/0/createSurface/components/2/path", pointer);
        Assert.Equal("img/b.png", path);
    }

    [Fact]
    public void ContentTypeFor_maps_browser_renderable_formats_only()
    {
        Assert.Equal("image/png", DysonA2uiImage.ContentTypeFor("a.PNG"));
        Assert.Equal("image/jpeg", DysonA2uiImage.ContentTypeFor("b.jpeg"));
        Assert.Equal("image/jpeg", DysonA2uiImage.ContentTypeFor("b.jpg"));
        Assert.Equal("image/gif", DysonA2uiImage.ContentTypeFor("c.gif"));
        Assert.Equal("image/webp", DysonA2uiImage.ContentTypeFor("d.webp"));
        Assert.Equal("image/bmp", DysonA2uiImage.ContentTypeFor("e.bmp"));
        foreach (var other in new[] { "x.svg", "x.tiff", "x.ico", "x.pdf", "noext" })
            Assert.Null(DysonA2uiImage.ContentTypeFor(other));
    }

    [Fact]
    public async Task LoadCard_returns_the_card_and_texts_for_escaping_missing_unsupported_and_oversized_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "img"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "img", "a.png"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(root, "a.svg"), [1]);

            var ok = await DysonA2uiImage.LoadCardAsync("img/a.png", null, root);
            Assert.True(ok.IsSuccess, ok.IsError ? ok.Error : null);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "img", "a.png")), ok.Value.AbsolutePath);
            Assert.Equal("image/png", ok.Value.ContentType);
            Assert.Equal("open_file:img/a.png", ok.Value.OpenFunc);
            Assert.Equal("a.png", ok.Value.Title);

            Assert.StartsWith("Path escapes work directory", (await DysonA2uiImage.LoadCardAsync("../x.png", null, root)).Error);
            Assert.True((await DysonA2uiImage.LoadCardAsync(Path.Combine(root, "img", "a.png"), null, root)).IsError);
            Assert.Equal("File not found: gone.png", (await DysonA2uiImage.LoadCardAsync("gone.png", null, root)).Error);
            Assert.StartsWith("Not a supported image file", (await DysonA2uiImage.LoadCardAsync("a.svg", null, root)).Error);

            await using (var big = File.Create(Path.Combine(root, "big.png")))
                big.SetLength(DysonA2uiImage.MaxBytes + 1);
            Assert.Equal("Image is larger than 20 MB: big.png", (await DysonA2uiImage.LoadCardAsync("big.png", null, root)).Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Preview_store_serves_the_image_content_type_for_the_resolved_file()
    {
        var store = new DysonFilePreviewStore();
        var abs = Path.Combine(Path.GetTempPath(), "a.png");
        var id = store.PutFile(abs, DysonA2uiImage.ContentTypeFor("a.png")!);
        Assert.True(store.TryGet(id, out var entry));
        Assert.Equal("image/png", entry.ContentType);
        Assert.Equal(abs, entry.FilePath);
    }
}

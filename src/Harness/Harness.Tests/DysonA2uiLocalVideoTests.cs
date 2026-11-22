using DysonHarness;

namespace Harness.Tests;

/// <summary>LocalVideo: catalog shape, open_file path rules, card data and card-text failures.</summary>
public class DysonA2uiLocalVideoTests
{
    private const string Dyson = DysonA2uiProtocol.DysonCatalogId;

    private static string Surface(string path) =>
        DysonA2uiValidatorTests.Create($$"""{"id":"root","component":"LocalVideo","@catalogId":"{{Dyson}}","path":"{{path}}","title":"Demo"}""");

    [Fact]
    public void Validator_accepts_LocalVideo_under_Dyson_catalog_only_and_lists_its_path()
    {
        var ok = DysonA2uiValidator.Parse(Surface("media/clip.mp4"));
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error.ToWireJson() : null);
        var (pointer, path) = Assert.Single(DysonA2uiLocalVideo.PathPointers(ok.Value));
        Assert.Equal("/messages/0/createSurface/components/0/path", pointer);
        Assert.Equal("media/clip.mp4", path);

        var basic = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create("""{"id":"root","component":"LocalVideo","path":"a.mp4"}"""));
        Assert.True(basic.IsError);

        var blank = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create($$"""{"id":"root","component":"LocalVideo","@catalogId":"{{Dyson}}","path":" "}"""));
        Assert.True(blank.IsError);
        Assert.EndsWith("/path", blank.Error.Path);
    }

    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("media/../../x.mp4")]
    [InlineData("")]
    public async Task ResolveRelative_rejects_paths_open_file_rejects(string path)
    {
        var root = NewRoot();
        try
        {
            var resolved = await DysonA2uiLocalVideo.ResolveRelativeAsync(path, root);
            Assert.True(resolved.IsError);
            var rooted = await DysonA2uiLocalVideo.ResolveRelativeAsync(Path.Combine(root, "a.mp4"), root);
            Assert.True(rooted.IsError);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LoadCard_returns_title_type_and_open_func_for_every_viewer_video_format()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "media"));
            foreach (var ext in new[] { ".mp4", ".M4V", ".webm", ".mov", ".mkv" })
            {
                var rel = "media/clip" + ext;
                await File.WriteAllBytesAsync(Path.Combine(root, "media", "clip" + ext), [0, 0, 0, 0x18]);

                var card = await DysonA2uiLocalVideo.LoadCardAsync(rel, null, root);
                Assert.True(card.IsSuccess, card.IsError ? card.Error : null);
                Assert.Equal("clip" + ext, card.Value.Title);
                Assert.Equal(rel, card.Value.RelativePath);
                Assert.Equal(DysonVideoFiles.ContentTypeFor(rel), card.Value.ContentType);
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "media", "clip" + ext)), card.Value.AbsolutePath);
                Assert.Equal("open_file:" + rel, card.Value.OpenFunc);

                var intent = await DysonBuiltInConversationActions.TryResolveAsync(card.Value.OpenFunc, root);
                Assert.Equal(DysonBuiltInConversationKind.OpenFile, intent!.Value.Kind);
                Assert.Equal(rel, intent.Value.PathOrUrl);
            }

            var titled = await DysonA2uiLocalVideo.LoadCardAsync("media/clip.mp4", "  Launch demo ", root);
            Assert.Equal("Launch demo", titled.Value.Title);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LoadCard_missing_unsupported_and_escaping_files_are_card_text_not_throws()
    {
        var root = NewRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "movie.ogv"), "x");

            var missing = await DysonA2uiLocalVideo.LoadCardAsync("gone.mp4", null, root);
            Assert.Equal("File not found: gone.mp4", missing.Error);

            var unsupported = await DysonA2uiLocalVideo.LoadCardAsync("movie.ogv", null, root);
            Assert.Equal("Not a supported video file: movie.ogv", unsupported.Error);

            var escaping = await DysonA2uiLocalVideo.LoadCardAsync("../x.mp4", null, root);
            Assert.StartsWith("Path escapes work directory", escaping.Error);

            var noRoot = await DysonA2uiLocalVideo.LoadCardAsync("a.mp4", null, null);
            Assert.True(noRoot.IsError);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-localvideo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

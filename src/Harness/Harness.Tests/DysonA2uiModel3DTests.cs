using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>Model3D: catalog shape (Dyson only, height bounds), open_file path rules, card data, card-text failures and the preview content type.</summary>
public class DysonA2uiModel3DTests
{
    private const string Dyson = DysonA2uiProtocol.DysonCatalogId;

    private static string Surface(string path, string extra = "") =>
        DysonA2uiValidatorTests.Create($$"""{"id":"root","component":"Model3D","@catalogId":"{{Dyson}}","path":"{{path}}","title":"Chair"{{extra}}}""");

    [Fact]
    public void Validator_accepts_Model3D_under_Dyson_catalog_only_with_bounded_height_and_lists_its_path()
    {
        var ok = DysonA2uiValidator.Parse(Surface("models/chair.glb", ",\"height\":320"));
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error.ToWireJson() : null);
        var (pointer, path) = Assert.Single(DysonA2uiLocalVideo.PathPointers(ok.Value, DysonA2uiModel3D.ComponentName));
        Assert.Equal("/messages/0/createSurface/components/0/path", pointer);
        Assert.Equal("models/chair.glb", path);
        Assert.Empty(DysonA2uiLocalVideo.PathPointers(ok.Value));

        Assert.True(DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create("""{"id":"root","component":"Model3D","path":"a.glb"}""", DysonA2uiValidatorTests.Basic)).IsError);

        var blank = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create($$"""{"id":"root","component":"Model3D","@catalogId":"{{Dyson}}","path":" "}"""));
        Assert.True(blank.IsError);
        Assert.EndsWith("/path", blank.Error.Path);

        foreach (var height in new[] { 100, 900 })
        {
            var bad = DysonA2uiValidator.Parse(Surface("a.glb", ",\"height\":" + height));
            Assert.True(bad.IsError, height.ToString());
            Assert.EndsWith("/height", bad.Error.Path);
        }
    }

    [Fact]
    public async Task LoadCard_types_glb_and_fbx_and_rejects_other_missing_escaping_and_oversized_files_as_text()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-model3d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        try
        {
            foreach (var name in new[] { "a.glb", "A.FBX", "b.gltf", "c.obj" })
                await File.WriteAllBytesAsync(Path.Combine(root, "models", name), [1, 2, 3]);

            var glb = await DysonA2uiModel3D.LoadCardAsync("models/a.glb", null, root);
            Assert.True(glb.IsSuccess, glb.IsError ? glb.Error : null);
            Assert.Equal("model/gltf-binary", glb.Value.ContentType);
            Assert.Equal("glb", glb.Value.Format);
            Assert.Equal("a.glb", glb.Value.Title);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "models", "a.glb")), glb.Value.AbsolutePath);

            var fbx = await DysonA2uiModel3D.LoadCardAsync("models/A.FBX", "  Dancer ", root);
            Assert.Equal("application/octet-stream", fbx.Value.ContentType);
            Assert.Equal("fbx", fbx.Value.Format);
            Assert.Equal("Dancer", fbx.Value.Title);

            foreach (var other in new[] { "models/b.gltf", "models/c.obj" })
                Assert.StartsWith("Not a supported 3D model file", (await DysonA2uiModel3D.LoadCardAsync(other, null, root)).Error);

            Assert.Equal("File not found: gone.glb", (await DysonA2uiModel3D.LoadCardAsync("gone.glb", null, root)).Error);
            Assert.StartsWith("Path escapes work directory", (await DysonA2uiModel3D.LoadCardAsync("../x.glb", null, root)).Error);
            Assert.True((await DysonA2uiModel3D.LoadCardAsync(Path.Combine(root, "models", "a.glb"), null, root)).IsError);

            await using (var big = File.Create(Path.Combine(root, "big.glb")))
                big.SetLength(DysonA2uiModel3D.MaxBytes + 1);
            Assert.Equal("3D model is larger than 64 MB.", (await DysonA2uiModel3D.LoadCardAsync("big.glb", null, root)).Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Preview_store_serves_the_model_content_type_for_the_resolved_file()
    {
        var store = new DysonFilePreviewStore();
        var abs = Path.Combine(Path.GetTempPath(), "chair.glb");
        var id = store.PutFile(abs, DysonA2uiModel3D.ContentTypeFor("a.glb")!);
        Assert.True(store.TryGet(id, out var entry));
        Assert.Equal("model/gltf-binary", entry.ContentType);
        Assert.Equal(abs, entry.FilePath);
        Assert.Equal("application/octet-stream", DysonA2uiModel3D.ContentTypeFor("x.Fbx"));
        Assert.Null(DysonA2uiModel3D.ContentTypeFor("x.gltf"));
    }
}

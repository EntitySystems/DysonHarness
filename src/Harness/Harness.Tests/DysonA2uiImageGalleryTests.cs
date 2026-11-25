using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Plan 384: ImageGallery url/path items, validator rules, path pointers, host rule, stepping, and the title helpers.</summary>
public class DysonA2uiImageGalleryTests
{
    private const string P0 = "/messages/0/createSurface/components/0";

    private static Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> Parse(string componentBody) =>
        DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create("{\"id\":\"root\",\"component\":\"ImageGallery\"," + componentBody + "}", "", "s1"));

    private static void AssertFails(string componentBody, string path)
    {
        var result = Parse(componentBody);
        Assert.True(result.IsError, "expected a validation error");
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.Equal(path, result.Error.Path);
    }

    private static void AssertValid(string componentBody)
    {
        var result = Parse(componentBody);
        Assert.True(result.IsSuccess, result.IsError ? result.Error.ToWireJson() : null);
    }

    private static string Many(int count) =>
        "\"images\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"\"img/{i}.png\"")) + "]";

    [Fact]
    public void Normalize_keeps_input_order_for_a_mixed_list()
    {
        var items = DysonA2uiImageGallery.Normalize(JsonNode.Parse(
            "[\"https://a/x.png\",\"img/b.png\",{\"path\":\"c.jpg\",\"alt\":\"C\"},{\"url\":\"https://d/y.webp\"}]"));

        Assert.Equal([0, 1, 2, 3], items.Select(i => i.Index));
        Assert.Equal(
            [DysonA2uiGalleryKind.Url, DysonA2uiGalleryKind.Path, DysonA2uiGalleryKind.Path, DysonA2uiGalleryKind.Url],
            items.Select(i => i.Kind));
        Assert.Equal(["https://a/x.png", "img/b.png", "c.jpg", "https://d/y.webp"], items.Select(i => i.Value));
        Assert.Equal("C", items[2].Alt);
    }

    [Fact]
    public void Normalize_makes_invalid_slides_for_bad_items_and_caps_at_50()
    {
        var items = DysonA2uiImageGallery.Normalize(JsonNode.Parse("[1,{\"url\":\"https://a/x.png\",\"path\":\"b.png\"},\"data:image/png;base64,AAAA\",{}]"));
        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.Equal(DysonA2uiGalleryKind.Invalid, i.Kind));

        Assert.Equal(DysonA2uiImageGallery.MaxImages, DysonA2uiImageGallery.Normalize(JsonNode.Parse(Many(60).Replace("\"images\":", ""))).Count);
        Assert.Empty(DysonA2uiImageGallery.Normalize(null));
        Assert.Empty(DysonA2uiImageGallery.Normalize(JsonNode.Parse("{\"@path\":\"/x\"}")));
    }

    [Fact]
    public void Normalize_carries_title_and_item_name_precedence()
    {
        var items = DysonA2uiImageGallery.Normalize(JsonNode.Parse(
            "[{\"path\":\"img/a.png\",\"title\":\"  A\\nTitle \",\"caption\":\"cap\",\"description\":\"desc\"},{\"path\":\"img/b.png\",\"title\":\"T\"},{\"url\":\"https://www.example.com/x.png\"}]"));

        Assert.Equal("A Title", items[0].Title);
        Assert.Equal("cap", items[0].Caption);
        Assert.Equal("desc", items[0].Name);
        Assert.Equal("T", items[1].Name);
        Assert.Equal("example.com", items[2].Name);
        Assert.Equal("b.png", DysonA2uiImageGallery.Normalize(JsonNode.Parse("[\"img/b.png\"]"))[0].Name);
    }

    [Fact]
    public void Check_object_needs_exactly_one_of_url_or_path()
    {
        AssertFails("\"images\":[\"a.png\",\"b.png\",{\"url\":\"https://a/x.png\",\"path\":\"c.png\"}]", P0 + "/images/2");
        // Old rows had an optional url: neither stays valid and renders as a text slide.
        AssertValid("\"images\":[\"a.png\",{\"caption\":\"c\"}]");
        var slide = DysonA2uiImageGallery.Normalize(JsonNode.Parse("[{\"title\":\"T\",\"caption\":\"c\"}]"))[0];
        Assert.Equal(DysonA2uiGalleryKind.Invalid, slide.Kind);
        Assert.Equal("T", slide.Value);
    }

    [Fact]
    public void Normalize_resolves_bound_item_text_and_IsLoadableUrl_applies_the_media_url_rule()
    {
        var items = DysonA2uiImageGallery.Normalize(
            JsonNode.Parse("[{\"path\":\"c.jpg\",\"title\":{\"@path\":\"/t\"}}]"),
            f => f is JsonObject ? JsonValue.Create("Bound") : f);
        Assert.Equal("Bound", items[0].Title);
        Assert.False(DysonA2uiImageGallery.IsLoadableUrl("https://example.com/" + new string('a', 2100)));
    }

    [Theory]
    [InlineData("{\"url\":\"data:image/png;base64,AAAA\"}", "/images/0/url")]
    [InlineData("{\"url\":\"ftp://a/x.png\"}", "/images/0/url")]
    [InlineData("\"ftp://a/x.png\"", "/images/0")]
    [InlineData("\"data:image/png;base64,AAAA\"", "/images/0")]
    [InlineData("\"  \"", "/images/0")]
    [InlineData("{\"path\":\"  \"}", "/images/0/path")]
    [InlineData("7", "/images/0")]
    public void Check_rejects_bad_items_at_the_exact_pointer(string item, string suffix) =>
        AssertFails("\"images\":[" + item + "]", P0 + suffix);

    [Fact]
    public void Check_caps_at_50_images()
    {
        AssertValid(Many(50));
        AssertFails(Many(51), P0 + "/images/50");
        Assert.Equal(("/images/50", "at most 50 images."), DysonA2uiImageGallery.Check(JsonNode.Parse("[" + string.Join(",", Enumerable.Repeat("\"a.png\"", 51)) + "]")));
        Assert.Null(DysonA2uiImageGallery.Check(JsonNode.Parse("{\"@path\":\"/x\"}")));
        Assert.Null(DysonA2uiImageGallery.Check(null));
    }

    [Theory]
    [InlineData("\"height\":100")]
    [InlineData("\"height\":900")]
    public void Height_outside_120_to_800_fails(string height) =>
        AssertFails("\"images\":[\"a.png\"]," + height, P0 + "/height");

    [Fact]
    public void Old_rows_and_new_shapes_validate()
    {
        AssertValid("\"images\":[{\"url\":\"https://example.com/a.png\",\"caption\":\"c\",\"title\":\"t\"}]");
        AssertValid("\"items\":[{\"url\":\"https://example.com/a.png\"}],\"columns\":3");
        AssertValid("\"images\":[],\"title\":\"Shots\",\"height\":400");
        AssertValid("\"images\":[\"a.png\",\"https://example.com/b.png\",{\"path\":\"c.jpg\",\"alt\":\"C\",\"description\":\"d\",\"title\":\"T\"}]");
        AssertValid("\"images\":[{\"path\":\"c.jpg\",\"title\":{\"@path\":\"/t\"},\"caption\":{\"@path\":\"/c\"}}]");
        AssertValid("\"images\":{\"@path\":\"/imgs\"}");
    }

    [Fact]
    public void Items_with_a_bad_item_fail_under_items()
    {
        AssertFails("\"items\":[{\"url\":\"https://a/x.png\",\"path\":\"c.png\"}]", P0 + "/items/0");
    }

    [Fact]
    public void ImageGrid_is_unchanged_and_still_rejects_path()
    {
        var result = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create("{\"id\":\"root\",\"component\":\"ImageGrid\",\"images\":[{\"path\":\"a.png\"}]}", "", "s1"));
        Assert.True(result.IsError);
        Assert.Equal(P0 + "/images/0/path", result.Error.Path);
    }

    [Fact]
    public void PathPointers_yield_string_and_object_paths_and_skip_urls()
    {
        var parsed = Parse("\"images\":[\"https://a/x.png\",\"img/b.png\",{\"path\":\"../c.jpg\"},{\"url\":\"https://d/y.webp\"}],\"title\":\"t\"");
        Assert.True(parsed.IsSuccess);

        var pointers = DysonA2uiImageGallery.PathPointers(parsed.Value);

        Assert.Equal(
            [(P0 + "/images/1", "img/b.png"), (P0 + "/images/2/path", "../c.jpg")],
            pointers.Select(p => (p.Pointer, p.Path)));
    }

    [Theory]
    [InlineData("https://example.com/a.png", true)]
    [InlineData("http://localhost/", false)]
    [InlineData("http://192.168.1.2/", false)]
    [InlineData("http://nas/", false)]
    [InlineData("https://x.local/", false)]
    [InlineData("https://u:p@h.com/", false)]
    [InlineData("", false)]
    public void IsLoadableUrl_follows_the_audio_host_rule(string url, bool expected) =>
        Assert.Equal(expected, DysonA2uiImageGallery.IsLoadableUrl(url));

    [Fact]
    public void RemoteHosts_are_distinct_in_order_and_skip_private_and_path_items()
    {
        var items = DysonA2uiImageGallery.Normalize(JsonNode.Parse(
            "[\"https://b.example.com/1.png\",\"img/p.png\",\"https://a.example.com/2.png\",\"https://b.example.com/3.png\",\"http://192.168.1.2/x.png\"]"));

        Assert.Equal(["b.example.com", "a.example.com"], DysonA2uiImageGallery.RemoteHosts(items));
    }

    [Fact]
    public void Step_clamps_inline_wraps_in_the_modal_and_is_safe_for_count_zero()
    {
        Assert.Equal(0, DysonA2uiImageGallery.Step(0, -1, 5, false));
        Assert.Equal(4, DysonA2uiImageGallery.Step(4, 1, 5, false));
        Assert.Equal(3, DysonA2uiImageGallery.Step(2, 1, 5, false));
        Assert.Equal(0, DysonA2uiImageGallery.Step(4, 1, 5, true));
        Assert.Equal(4, DysonA2uiImageGallery.Step(0, -1, 5, true));
        Assert.Equal(0, DysonA2uiImageGallery.Step(3, 1, 0, true));
        Assert.Equal(0, DysonA2uiImageGallery.Step(3, 1, 0, false));
    }

    [Fact]
    public void ClampHeight_defaults_to_320_and_clamps()
    {
        Assert.Equal(320, DysonA2uiImageGallery.ClampHeight(null));
        Assert.Equal(120, DysonA2uiImageGallery.ClampHeight(10));
        Assert.Equal(800, DysonA2uiImageGallery.ClampHeight(5000));
        Assert.Equal(400, DysonA2uiImageGallery.ClampHeight(400));
    }

    [Fact]
    public void NormalizeTitle_trims_collapses_and_truncates()
    {
        Assert.Null(DysonA2uiImage.NormalizeTitle(null));
        Assert.Null(DysonA2uiImage.NormalizeTitle(""));
        Assert.Null(DysonA2uiImage.NormalizeTitle("  \n\t "));
        Assert.Equal("a b", DysonA2uiImage.NormalizeTitle("  a\n b "));
        var exact = new string('x', 200);
        Assert.Equal(exact, DysonA2uiImage.NormalizeTitle(exact));
        var cut = DysonA2uiImage.NormalizeTitle(new string('x', 201));
        Assert.Equal(200, cut!.Length);
        Assert.Equal(new string('x', 199) + "…", cut);
    }

    [Fact]
    public void AccessibleName_takes_the_first_non_blank_with_title_before_the_file_name()
    {
        Assert.Equal("alt", DysonA2uiImage.AccessibleName("alt", "desc", "fb", "title", "f.png"));
        Assert.Equal("desc", DysonA2uiImage.AccessibleName(" ", "desc", "fb", "title", "f.png"));
        Assert.Equal("fb", DysonA2uiImage.AccessibleName(null, null, "fb", "title", "f.png"));
        Assert.Equal("title", DysonA2uiImage.AccessibleName(null, null, null, "title", "f.png"));
        Assert.Equal("f.png", DysonA2uiImage.AccessibleName(null, "", null, " ", "f.png"));
        Assert.Null(DysonA2uiImage.AccessibleName(null, null, null, null, null));
    }

    [Fact]
    public void Image_with_a_title_validates_and_ImageGallery_columns_are_still_accepted()
    {
        var image = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create("{\"id\":\"root\",\"component\":\"Image\",\"path\":\"a.png\",\"title\":\"T\"}", "", "s1"));
        Assert.True(image.IsSuccess, image.IsError ? image.Error.ToWireJson() : null);
        var bound = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create("{\"id\":\"root\",\"component\":\"Image\",\"path\":\"a.png\",\"title\":{\"@path\":\"/t\"}}", "", "s1"));
        Assert.True(bound.IsSuccess, bound.IsError ? bound.Error.ToWireJson() : null);
    }
}

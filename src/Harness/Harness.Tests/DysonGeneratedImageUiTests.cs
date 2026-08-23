using Harness.UI.Demo;

namespace Harness.Tests;

public class DysonGeneratedImageUiTests
{
    [Fact]
    public void Generated_image_preview_helper_recognizes_only_png_signature()
    {
        Assert.True(DysonGeneratedImagePreview.LooksLikePng(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.False(DysonGeneratedImagePreview.LooksLikePng([0x89, 0x50, 0x4E, 0x47]));
        Assert.False(DysonGeneratedImagePreview.LooksLikePng("not an image"u8));
    }

    [Fact]
    public void Generate_image_tool_row_summary_uses_artifact_count_not_prompt()
    {
        var summary = DysonToolCallUi.GetCollapsedSummary(
            "GenerateImage",
            "{\"prompt\":\"private prompt content\"}",
            "{\"artifactCount\":2}",
            hasResult: true);

        Assert.Equal("2 generated images", summary.Text);
        Assert.DoesNotContain("private prompt", summary.Text, StringComparison.OrdinalIgnoreCase);
    }
}

using DysonHarness;

namespace Harness.Tests;

/// <summary>Plan 384 D8: zoom grid, contain-fit, pan bounds and clamp.</summary>
public class DysonA2uiImageZoomTests
{
    [Theory]
    [InlineData(50, 100)]
    [InlineData(100, 100)]
    [InlineData(505, 500)]
    [InlineData(1000, 500)]
    public void ClampPercent_stays_in_100_to_500(int input, int expected) =>
        Assert.Equal(expected, DysonA2uiImageZoom.ClampPercent(input));

    [Theory]
    [InlineData(100, 1, 110)]
    [InlineData(100, -1, 100)]
    [InlineData(500, 1, 500)]
    [InlineData(495, 1, 500)]
    [InlineData(503, -1, 490)]
    [InlineData(105, 1, 110)]
    [InlineData(105, -1, 100)]
    [InlineData(250, -1, 240)]
    [InlineData(250, 0, 250)]
    [InlineData(10, 1, 110)]
    public void Step_snaps_to_the_10_grid_and_clamps(int current, int direction, int expected) =>
        Assert.Equal(expected, DysonA2uiImageZoom.Step(current, direction));

    [Fact]
    public void Fit_contains_a_landscape_image_in_a_portrait_view_and_never_upscales()
    {
        Assert.Equal((200.0, 100.0), DysonA2uiImageZoom.Fit(2000, 1000, 200, 400));
        Assert.Equal((100.0, 50.0), DysonA2uiImageZoom.Fit(100, 50, 800, 600));
        Assert.Equal((0.0, 0.0), DysonA2uiImageZoom.Fit(0, 100, 200, 200));
        Assert.Equal((0.0, 0.0), DysonA2uiImageZoom.Fit(100, 100, 0, 200));
        Assert.Equal((0.0, 0.0), DysonA2uiImageZoom.Fit(-1, 100, 200, 200));
    }

    [Fact]
    public void PanBounds_are_zero_at_100_percent_and_for_a_small_image_and_exact_when_zoomed()
    {
        Assert.Equal((0.0, 0.0), DysonA2uiImageZoom.PanBounds(400, 300, 400, 300, 100));
        Assert.Equal((0.0, 0.0), DysonA2uiImageZoom.PanBounds(100, 50, 800, 600, 200));
        Assert.Equal((200.0, 150.0), DysonA2uiImageZoom.PanBounds(400, 300, 400, 300, 200));
        Assert.Equal((800.0, 600.0), DysonA2uiImageZoom.PanBounds(400, 300, 400, 300, 500));
        Assert.Equal((200.0, 0.0), DysonA2uiImageZoom.PanBounds(400, 100, 400, 300, 200));
    }

    [Fact]
    public void ClampPan_limits_both_axes_and_signs()
    {
        var bounds = (MaxX: 50.0, MaxY: 20.0);
        Assert.Equal((50.0, -20.0), DysonA2uiImageZoom.ClampPan(80, -90, bounds));
        Assert.Equal((-50.0, 20.0), DysonA2uiImageZoom.ClampPan(-80, 90, bounds));
        Assert.Equal((10.0, -5.0), DysonA2uiImageZoom.ClampPan(10, -5, bounds));
        Assert.Equal((0.0, 0.0), DysonA2uiImageZoom.ClampPan(30, -30, (0, 0)));
    }

    [Fact]
    public void DoubleClick_toggles_100_and_200()
    {
        Assert.Equal(200, DysonA2uiImageZoom.DoubleClick(100));
        Assert.Equal(100, DysonA2uiImageZoom.DoubleClick(250));
        Assert.Equal(100, DysonA2uiImageZoom.DoubleClick(200));
    }

    [Fact]
    public void Reset_and_ValueText()
    {
        Assert.Equal((100, 0.0, 0.0), DysonA2uiImageZoom.Reset());
        Assert.Equal("150%", DysonA2uiImageZoom.ValueText(150));
        Assert.Equal("500%", DysonA2uiImageZoom.ValueText(900));
        Assert.Equal(40, DysonA2uiImageZoom.KeyPanPx);
    }
}

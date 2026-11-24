namespace DysonHarness;

/// <summary>
/// Zoom and pan math for the image modal (the JS module only measures and applies it). Pure, total functions:
/// bad input clamps, nothing throws.
/// </summary>
public static class DysonA2uiImageZoom
{
    public const int MinPercent = 100;
    public const int MaxPercent = 500;
    public const int StepPercent = 10;
    public const int DoubleClickPercent = 200;
    public const int KeyPanPx = 40;

    public static int ClampPercent(int percent) => Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>Next zoom on the 10% grid, clamped. <paramref name="direction"/> is the sign: + zooms in, - zooms out.</summary>
    public static int Step(int current, int direction)
    {
        var c = ClampPercent(current);
        var next = Math.Sign(direction) switch
        {
            > 0 => ((c / StepPercent) + 1) * StepPercent,
            < 0 => (((c + StepPercent - 1) / StepPercent) - 1) * StepPercent,
            _ => c,
        };
        return ClampPercent(next);
    }

    /// <summary>
    /// The 100% size of the image in the view: contained, never upscaled (same as <c>max-width/max-height: 100%</c>).
    /// Zero or negative sizes give (0, 0).
    /// </summary>
    public static (double Width, double Height) Fit(double naturalW, double naturalH, double viewW, double viewH)
    {
        if (naturalW <= 0 || naturalH <= 0 || viewW <= 0 || viewH <= 0)
            return (0, 0);
        var scale = Math.Min(1, Math.Min(viewW / naturalW, viewH / naturalH));
        return (naturalW * scale, naturalH * scale);
    }

    /// <summary>How far the scaled image may be dragged from the centre: <c>max(0, (fit * zoom - view) / 2)</c> per axis.</summary>
    public static (double MaxX, double MaxY) PanBounds(double fitW, double fitH, double viewW, double viewH, int percent)
    {
        var zoom = ClampPercent(percent) / 100.0;
        return (Math.Max(0, (fitW * zoom - viewW) / 2), Math.Max(0, (fitH * zoom - viewH) / 2));
    }

    public static (double X, double Y) ClampPan(double x, double y, (double MaxX, double MaxY) bounds) =>
        (Math.Clamp(x, -bounds.MaxX, bounds.MaxX), Math.Clamp(y, -bounds.MaxY, bounds.MaxY));

    public static (int Percent, double PanX, double PanY) Reset() => (MinPercent, 0, 0);

    /// <summary>100% goes to 200%; anything zoomed goes back to 100%.</summary>
    public static int DoubleClick(int current) => ClampPercent(current) <= MinPercent ? DoubleClickPercent : MinPercent;

    public static string ValueText(int percent) => $"{ClampPercent(percent)}%";
}

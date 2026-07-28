namespace DysonHarness;

/// <summary>
/// Maps a DIP rubber-band selection over browser content to a pixel rect inside a full-viewport screenshot.
/// </summary>
public static class DysonBrowserSnipCrop
{
    /// <summary>
    /// Converts DIP selection bounds (relative to content host) into a clamped pixel crop rect.
    /// Returns null when the mapped size is empty or inputs are invalid.
    /// </summary>
    public static (int X, int Y, int Width, int Height)? MapDipSelectionToPixelRect(
        double selectionX,
        double selectionY,
        double selectionWidth,
        double selectionHeight,
        double contentWidthDip,
        double contentHeightDip,
        int shotWidthPx,
        int shotHeightPx)
    {
        if (selectionWidth <= 0
            || selectionHeight <= 0
            || contentWidthDip <= 0
            || contentHeightDip <= 0
            || shotWidthPx <= 0
            || shotHeightPx <= 0)
        {
            return null;
        }

        var scaleX = shotWidthPx / contentWidthDip;
        var scaleY = shotHeightPx / contentHeightDip;

        var x = (int)Math.Floor(selectionX * scaleX);
        var y = (int)Math.Floor(selectionY * scaleY);
        var right = (int)Math.Ceiling((selectionX + selectionWidth) * scaleX);
        var bottom = (int)Math.Ceiling((selectionY + selectionHeight) * scaleY);

        x = Math.Clamp(x, 0, shotWidthPx - 1);
        y = Math.Clamp(y, 0, shotHeightPx - 1);
        right = Math.Clamp(right, x + 1, shotWidthPx);
        bottom = Math.Clamp(bottom, y + 1, shotHeightPx);

        var width = right - x;
        var height = bottom - y;
        if (width <= 0 || height <= 0)
            return null;

        return (x, y, width, height);
    }
}

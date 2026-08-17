using System.Globalization;

namespace DysonHarness;

/// <summary>
/// Maps a DIP rubber-band selection over browser content to a pixel rect inside a full-viewport screenshot.
/// </summary>
public static class DysonBrowserSnipCrop
{
    /// <summary>
    /// Document Y of the snip (CSS px): scrollY plus the rubber-band top mapped into the viewport.
    /// </summary>
    public static double DocumentY(
        double scrollY,
        double selectionY,
        double contentHeightDip,
        double viewportHeight)
    {
        if (contentHeightDip <= 0)
            return scrollY;
        return scrollY + (selectionY / contentHeightDip) * viewportHeight;
    }

    /// <summary>
    /// Percent of page height for a document Y (0–100). <paramref name="scrollHeight"/> is floored at 1.
    /// </summary>
    public static int PercentDownThePage(double documentY, double scrollHeight)
    {
        var percent = (int)Math.Round(
            100.0 * documentY / Math.Max(scrollHeight, 1.0),
            MidpointRounding.AwayFromZero);
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// One composer line: <c>Snip: {url} · {n}% down the page</c>, dropping missing parts.
    /// Returns null when both URL and percent are absent.
    /// </summary>
    public static string? FormatPromptLine(string? url, int? percentDown)
    {
        var trimmed = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        var percent = percentDown is int n
            ? n.ToString(CultureInfo.InvariantCulture)
            : null;

        if (trimmed is not null && percent is not null)
            return $"Snip: {trimmed} · {percent}% down the page";
        if (trimmed is not null)
            return $"Snip: {trimmed}";
        if (percent is not null)
            return $"Snip: {percent}% down the page";
        return null;
    }

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

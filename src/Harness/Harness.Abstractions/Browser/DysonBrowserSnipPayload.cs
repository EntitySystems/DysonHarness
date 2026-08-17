namespace DysonHarness;

/// <summary>
/// Cropped page screenshot from the Dyson browser Snip chrome control.
/// Raised on <see cref="IDysonBrowserControl.SnipCaptured"/> for the UI host to queue as a pending composer image
/// and append a URL/scroll prompt line.
/// </summary>
public sealed class DysonBrowserSnipPayload
{
    public required byte[] ImageBytes { get; init; }

    /// <summary>
    /// Reserved for a future DOM-reference feature (elements intersecting the snip).
    /// Empty/null today — not resolved yet.
    /// </summary>
    public string? HtmlRef { get; init; }

    public string FileName { get; init; } = "browser-snip.jpg";

    /// <summary>Page URL captured when snip mode started (<see cref="IDysonBrowserTab"/> address).</summary>
    public string? Url { get; init; }

    /// <summary>window.scrollY in CSS px at snip enter; null when the JS probe failed.</summary>
    public double? ScrollY { get; init; }

    /// <summary>documentElement.scrollHeight in CSS px at snip enter; null when the JS probe failed.</summary>
    public double? ScrollHeight { get; init; }

    /// <summary>window.innerHeight in CSS px at snip enter; null when the JS probe failed.</summary>
    public double? ViewportHeight { get; init; }

    /// <summary>
    /// Document position of the rubber-band top as 0–100% of page height.
    /// Null when scroll metrics were not captured.
    /// </summary>
    public int? PercentDown { get; init; }
}

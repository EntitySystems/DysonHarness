namespace DysonHarness;

/// <summary>
/// Cropped page screenshot from the Dyson browser Snip chrome control.
/// Raised on <see cref="IDysonBrowserControl.SnipCaptured"/> for the UI host to queue as a pending composer image.
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
}

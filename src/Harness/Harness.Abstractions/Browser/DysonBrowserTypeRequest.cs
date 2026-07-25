namespace DysonHarness;

/// <summary>
/// Type text into a focused/selected element. Prefer <see cref="Selector"/> when known.
/// </summary>
public sealed class DysonBrowserTypeRequest
{
    public string? Selector { get; init; }
    public required string Text { get; init; }
    public bool ClearFirst { get; init; }
    public int? DelayMs { get; init; }
    public int? TimeoutMs { get; init; }
}

namespace DysonHarness;

/// <summary>Keyboard key press (optionally with modifiers / target selector).</summary>
public sealed class DysonBrowserKeyRequest
{
    public string? Selector { get; init; }

    /// <summary>Key name or character (e.g. Enter, Escape, a).</summary>
    public required string Key { get; init; }

    public bool CtrlKey { get; init; }
    public bool ShiftKey { get; init; }
    public bool AltKey { get; init; }
    public bool MetaKey { get; init; }

    public int? TimeoutMs { get; init; }
}

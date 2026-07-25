namespace DysonHarness;

/// <summary>
/// Click target: provide <see cref="Selector"/> and/or coordinates (<see cref="X"/>/<see cref="Y"/>).
/// </summary>
public sealed class DysonBrowserClickRequest
{
    public string? Selector { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }

    /// <summary>left | middle | right (default left).</summary>
    public string Button { get; init; } = "left";

    public bool CtrlKey { get; init; }
    public bool ShiftKey { get; init; }
    public bool AltKey { get; init; }
    public bool MetaKey { get; init; }

    public int? TimeoutMs { get; init; }
}

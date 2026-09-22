namespace Harness.UI.Components.Chat;

/// <summary>
/// Effort choices for a <see cref="ComposerEffortPicker"/>: the selected slug's registered
/// reasoning modes, plus the current value when it is not one of them.
/// </summary>
/// <remarks>
/// Keeping the current value visible matters because effort is free-form at the API boundary:
/// a session can carry an effort the slug no longer registers (slug edited, effort set before
/// the modes were trimmed). Dropping it from the menu would hide the active setting and leave
/// the user unable to re-pick it after switching away.
/// </remarks>
public static class ComposerEffortOptions
{
    public static IEnumerable<string> Build(IReadOnlyList<string>? modes, string? current)
    {
        modes ??= [];
        foreach (var mode in modes)
            yield return mode;

        var trimmed = string.IsNullOrWhiteSpace(current) ? null : current.Trim();
        if (trimmed is not null && !modes.Contains(trimmed, StringComparer.Ordinal))
            yield return trimmed;
    }
}

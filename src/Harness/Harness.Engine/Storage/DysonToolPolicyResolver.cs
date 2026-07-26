namespace DysonHarness;

/// <summary>
/// Resolves the effective disabled-tool set for a session mode.
/// v1 applies mode denylist only; <paramref name="modelSlugId"/> is accepted for future overlays.
/// </summary>
public static class DysonToolPolicyResolver
{
    private static readonly IReadOnlySet<string> Empty =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Returns the mode denylist from <paramref name="document"/>.
    /// Missing document / mode ⇒ empty set (all tools enabled).
    /// </summary>
    /// <remarks>
    /// ponytail: <paramref name="modelSlugId"/> is unused — do not merge <see cref="DysonToolPolicyDocument.Models"/> until a models UI lands.
    /// </remarks>
    public static IReadOnlySet<string> Resolve(
        DysonToolPolicyDocument? document,
        string agentMode,
        Guid? modelSlugId = null)
    {
        _ = modelSlugId; // plumbing only

        if (document is null || string.IsNullOrWhiteSpace(agentMode))
            return Empty;

        if (!document.Modes.TryGetValue(agentMode.Trim(), out var entry)
            || entry.DisabledTools is not { Count: > 0 })
            return Empty;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in entry.DisabledTools)
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name.Trim());
        }

        return set.Count == 0 ? Empty : set;
    }
}

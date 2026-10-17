namespace DysonHarness;

/// <summary>Picks the speech model row for one capability. Callers resolve STT and TTS separately.</summary>
public static class DysonSpeechModelSelection
{
    /// <summary>
    /// The preferred row when it is enabled, installed, and has <paramref name="capability"/>;
    /// else the single such row; else null.
    /// </summary>
    public static DysonSpeechModelEntity? Select(
        IReadOnlyList<DysonSpeechModelEntity> rows,
        DysonSpeechCapability capability,
        string? preferredRuntimeId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (capability == DysonSpeechCapability.None)
            return null;

        bool Usable(DysonSpeechModelEntity m) => m.IsEnabled && m.IsInstalled && m.Capability.HasFlag(capability);

        if (!string.IsNullOrWhiteSpace(preferredRuntimeId))
        {
            var preferred = rows.FirstOrDefault(m =>
                Usable(m) && string.Equals(m.RuntimeId, preferredRuntimeId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        var usable = rows.Where(Usable).Take(2).ToList();
        return usable.Count == 1 ? usable[0] : null;
    }
}

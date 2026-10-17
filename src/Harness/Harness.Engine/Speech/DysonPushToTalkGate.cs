namespace DysonHarness;

public enum DysonPushToTalkState
{
    /// <summary>Not a Meta Agent composer: no mic button.</summary>
    Hidden,

    /// <summary>Meta Agent composer, but no installed speech-to-text row: the button links to Settings → Voice.</summary>
    NotInstalled,

    /// <summary>Hold the button or Ctrl+M to record.</summary>
    Ready,
}

/// <summary>Meta Agent composer push-to-talk rules.</summary>
public static class DysonPushToTalkGate
{
    public const string VoiceSettingsHref = "/settings/voice";

    public static DysonPushToTalkState Evaluate(
        string? agentMode,
        IReadOnlyList<DysonSpeechModelEntity> rows,
        string? preferredInputRuntimeId)
    {
        if (!DysonSessionPolicy.IsMetaAgent(agentMode))
            return DysonPushToTalkState.Hidden;

        return DysonSpeechModelSelection.Select(rows, DysonSpeechCapability.Input, preferredInputRuntimeId) is null
            ? DysonPushToTalkState.NotInstalled
            : DysonPushToTalkState.Ready;
    }

    /// <summary>
    /// Inserts <paramref name="transcript"/> at <paramref name="caret"/> without replacing existing text,
    /// adding a space on either side when the neighbor is not whitespace. Returns the new text and caret.
    /// </summary>
    public static (string Text, int Caret) InsertAtCaret(string? text, int caret, string? transcript)
    {
        var current = text ?? "";
        var at = Math.Clamp(caret, 0, current.Length);
        var insert = (transcript ?? "").Trim();
        if (insert.Length == 0)
            return (current, at);

        var before = current[..at];
        var after = current[at..];
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]))
            insert = " " + insert;
        if (after.Length > 0 && !char.IsWhiteSpace(after[0]))
            insert += " ";

        return (before + insert + after, at + insert.Length);
    }
}

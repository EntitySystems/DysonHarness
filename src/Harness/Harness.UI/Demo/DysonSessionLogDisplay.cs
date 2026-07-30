namespace Harness.UI.Demo;

/// <summary>UI presentation level for a session log line (inferred from message text).</summary>
public enum DysonSessionLogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>One rail Session log row: level badge + wrapped body.</summary>
public sealed record DysonSessionLogRow(DysonSessionLogLevel Level, string Badge, string Body);

/// <summary>
/// Classifies plain <c>SnapshotLog</c> strings into info/warn/error for the Home rail panel.
/// Heuristic only — does not change engine log persistence.
/// </summary>
public static class DysonSessionLogDisplay
{
    public static DysonSessionLogRow Parse(string line)
    {
        var body = (line ?? string.Empty).Trim();
        var level = Classify(body);
        return new DysonSessionLogRow(level, BadgeFor(level), body);
    }

    private static DysonSessionLogLevel Classify(string body)
    {
        // ponytail: keyword scan is enough for rail presentation; upgrade if AppendLog gains structured levels
        if (ContainsAny(body, "failed", "exception", "fatal", " 404 ", "not found"))
            return DysonSessionLogLevel.Error;

        if (ContainsAny(body, "retry", "soft-pause", "skip", "fallback", "nudge", "missing", "dropped", "warn"))
            return DysonSessionLogLevel.Warn;

        return DysonSessionLogLevel.Info;
    }

    private static string BadgeFor(DysonSessionLogLevel level) => level switch
    {
        DysonSessionLogLevel.Error => "error",
        DysonSessionLogLevel.Warn => "warn",
        _ => "info",
    };

    private static bool ContainsAny(string body, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (body.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

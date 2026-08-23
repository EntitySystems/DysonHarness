namespace DysonHarness;

/// <summary>Shared defaults / clamps for session and slug max target context tokens.</summary>
public static class DysonMaxTargetContextTokens
{
    /// <summary>Harness default when session and slug defaults are unset.</summary>
    public const int HarnessDefault = 100_000;

    /// <summary>Composer ± stepper increment.</summary>
    public const int Step = 10_000;

    /// <summary>Upper clamp for session / slug max-target values.</summary>
    public const int Ceiling = 1_000_000;

    /// <summary>
    /// Cascade: session override if set → slug default if set → <see cref="HarnessDefault"/>.
    /// 0 means Off / unlimited (no DropContext inject).
    /// </summary>
    public static int Resolve(int? sessionOverride, int? slugDefault)
    {
        if (sessionOverride is int session)
            return session;
        if (slugDefault is int slug)
            return slug;
        return HarnessDefault;
    }

    /// <summary>Null clears; otherwise clamp to 0…<see cref="Ceiling"/>.</summary>
    public static int? Normalize(int? value)
    {
        if (value is null)
            return null;
        if (value.Value < 0)
            return 0;
        if (value.Value > Ceiling)
            return Ceiling;
        return value.Value;
    }

    /// <summary>Format for UI steppers / chips (e.g. <c>100K</c>, <c>1.4M</c>, <c>3B</c>). Use <paramref name="zeroAsOff"/> for max-target display.</summary>
    public static string FormatCompact(int tokens, bool zeroAsOff = false) =>
        FormatCompact((long)tokens, zeroAsOff);

    /// <summary>Format token counts using K, M, and B suffixes.</summary>
    public static string FormatCompact(long tokens, bool zeroAsOff = false)
    {
        if (tokens <= 0)
            return zeroAsOff ? "Off" : "0";

        if (tokens >= 1_000_000_000)
            return FormatScaled(tokens, 1_000_000_000, "B");

        if (tokens >= 1_000_000)
            return FormatScaled(tokens, 1_000_000, "M");

        if (tokens >= 1_000)
            return FormatScaled(tokens, 1_000, "K");

        return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatScaled(long tokens, long divisor, string suffix) =>
        $"{(tokens / (double)divisor).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}{suffix}";
}

namespace DysonHarness;

/// <summary>
/// EF row for table <c>session_secrets</c>: one secret the user typed into a masked RequestSecureData field.
/// Stored plaintext (same stance as provider tokens in <c>app_settings</c>). <see cref="Secret"/> is never
/// logged and never copied onto a turn.
/// </summary>
public sealed class DysonSessionSecretEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning subject (<see cref="DysonSubjects.Local"/> or cloud subject id).</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>The root Meta session that owns the store. Cascade-deleted with the session.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Stable id the model uses (<see cref="DysonSessionSecretLimits.IsValidKey"/>). Unique per session.</summary>
    public string Key { get; set; } = "";

    /// <summary>Label the user saw. Not the secret.</summary>
    public string Description { get; set; } = "";

    /// <summary>The value exactly as typed. Never logged.</summary>
    public string Secret { get; set; } = "";

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonSessionEntity? Session { get; set; }
}

/// <summary>Shared limits for <see cref="DysonSessionSecretEntity"/>.</summary>
public static class DysonSessionSecretLimits
{
    public const int MaxKeyLength = 64;
    public const int MaxDescriptionLength = 200;
    public const int MaxSecretLength = 8192;

    /// <summary>1..64 chars of <c>[A-Za-z0-9._-]</c>. Callers trim first.</summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            return false;

        foreach (var c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        }

        return true;
    }
}

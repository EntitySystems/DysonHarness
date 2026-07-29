namespace DysonHarness;

/// <summary>
/// Forever subject cookie used in <see cref="DysonHostingMode.Cloud"/>.
/// Wave 2b middleware owns mint/read/write; this type only names the cookie and documents policy.
/// </summary>
/// <remarks>
/// <para>
/// Behavior (implement in UI middleware, not here):
/// </para>
/// <list type="bullet">
/// <item>Cookie name <see cref="Name"/> (<c>dyson-subject</c>).</item>
/// <item>HttpOnly; SameSite=Lax; Secure when the request is HTTPS.</item>
/// <item>Far-future expiry via <see cref="ForeverLifetime"/> / <see cref="ForeverExpiresUtc"/> (≈ 10 years).</item>
/// <item>If missing or invalid → mint a new subject id (Guid string), ensure the subject row, set the cookie.</item>
/// <item>Never mint or accept <see cref="DysonSubjects.Shared"/> as a cookie value.</item>
/// <item>When users exist later, cookie subject must equal the authenticated user’s bound subject.</item>
/// </list>
/// </remarks>
public static class DysonSubjectCookie
{
    /// <summary>HTTP cookie name holding the cloud subject id.</summary>
    public const string Name = "dyson-subject";

    /// <summary>Suggested max-age for a “forever” subject cookie (10 years).</summary>
    public static readonly TimeSpan ForeverLifetime = TimeSpan.FromDays(3650);

    /// <summary>UTC expiry timestamp for a newly issued forever cookie.</summary>
    public static DateTimeOffset ForeverExpiresUtc(DateTimeOffset? utcNow = null)
        => (utcNow ?? DateTimeOffset.UtcNow).Add(ForeverLifetime);
}

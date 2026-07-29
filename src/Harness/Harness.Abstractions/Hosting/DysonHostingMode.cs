namespace DysonHarness;

/// <summary>
/// Deployment hosting mode. Bound from config section <see cref="DysonHostingOptions.SectionName"/>
/// key <c>Mode</c> (default <see cref="Local"/>).
/// </summary>
public enum DysonHostingMode
{
    /// <summary>Desktop / single-user: fixed <see cref="DysonSubjects.Local"/> subject, no cookie.</summary>
    Local = 0,

    /// <summary>Multi-subject host: forever <see cref="DysonSubjectCookie"/> binds the scoped subject.</summary>
    Cloud = 1,
}

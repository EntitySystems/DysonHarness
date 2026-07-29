namespace DysonHarness;

/// <summary>
/// <see cref="DysonHostingMode.Local"/> subject context: always <see cref="DysonSubjects.Local"/>.
/// Register as singleton.
/// </summary>
public sealed class DysonFixedLocalSubjectContext : IDysonSubjectContext
{
    /// <summary>Shared instance for hosts that do not need DI construction.</summary>
    public static DysonFixedLocalSubjectContext Instance { get; } = new();

    public string SubjectId => DysonSubjects.Local;
}

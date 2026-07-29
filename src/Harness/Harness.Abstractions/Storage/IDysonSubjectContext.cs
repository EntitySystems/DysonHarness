namespace DysonHarness;

/// <summary>
/// Current persistence subject for repository scoping.
/// Future: may gain user id / roles (or a sibling principal); SubjectId only for now.
/// </summary>
public interface IDysonSubjectContext
{
    /// <summary>Active subject id (e.g. <see cref="DysonSubjects.Local"/> or a cloud-minted Guid string).</summary>
    string SubjectId { get; }
}

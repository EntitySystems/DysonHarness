namespace DysonHarness;

/// <summary>
/// Mutable subject holder for <see cref="DysonHostingMode.Cloud"/>.
/// Register scoped; cookie middleware (Wave 2b) calls <see cref="SetSubjectId"/> once per request/circuit.
/// Also exposed as <see cref="IDysonSubjectContext"/> so repositories resolve the same instance.
/// </summary>
public sealed class DysonScopedSubjectContext : IDysonSubjectContext
{
    private string? _subjectId;

    /// <summary>True after <see cref="SetSubjectId"/> has been called for this scope.</summary>
    public bool IsSet => _subjectId is not null;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Subject was not set for this scope.</exception>
    public string SubjectId =>
        _subjectId ?? throw new InvalidOperationException(
            "Cloud subject context was not set. Wave 2b cookie middleware must call SetSubjectId before repositories run.");

    /// <summary>
    /// Binds the active subject for this scope. Rejects null/whitespace and
    /// <see cref="DysonSubjects.Shared"/> (never a cookie subject).
    /// </summary>
    public void SetSubjectId(string subjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (string.Equals(subjectId, DysonSubjects.Shared, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{DysonSubjects.Shared}' is not a valid cookie/subject context id.",
                nameof(subjectId));
        }

        _subjectId = subjectId;
    }
}

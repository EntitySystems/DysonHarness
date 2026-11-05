namespace DysonHarness;

/// <summary>
/// Picks the first provider that claims a git remote. A null or empty origin is NotDetected;
/// gh availability is not part of detection.
/// </summary>
public static class DysonVersionControlDetector
{
    public static Result<(IVersionControlProvider Provider, DysonPipelineTarget Target), DysonPipelineError> Detect(
        IEnumerable<IVersionControlProvider> providers, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return NotDetected("No git remote.");

        foreach (var provider in providers)
        {
            var resolved = provider.TryResolveTarget(origin);
            if (resolved.IsSuccess)
                return Result<(IVersionControlProvider, DysonPipelineTarget), DysonPipelineError>
                    .AsValue((provider, resolved.Value));
        }

        return NotDetected("No pipeline provider for this remote.");
    }

    private static Result<(IVersionControlProvider, DysonPipelineTarget), DysonPipelineError> NotDetected(string message) =>
        Result<(IVersionControlProvider, DysonPipelineTarget), DysonPipelineError>.AsError(
            new DysonPipelineError(DysonPipelineErrorKind.NotDetected, message));
}

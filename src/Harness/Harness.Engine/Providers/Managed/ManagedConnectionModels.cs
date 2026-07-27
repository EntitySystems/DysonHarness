namespace DysonHarness;

public enum ManagedEndpointKind
{
    OpenAiCompatible = 0,
    AnthropicCompatible = 1,
}

public sealed record ManagedConnectionBegin(
    string AuthUrl,
    string State,
    string? UserCode = null,
    string? Flow = null,
    int? ExpiresIn = null);

public sealed record ManagedConnectionComplete(
    string Status,
    bool IsComplete,
    string? Message = null);

public sealed record ManagedConnectionVerify(
    Guid ProviderId,
    int SlugCount,
    IReadOnlyList<string> Slugs);

namespace DysonHarness;

/// <summary>
/// Direct API-key managed inference (non-CLIProxy). Enable/disable stays on
/// <see cref="IDysonModelRepository"/>.
/// </summary>
public interface IManagedInferenceProvider
{
    string ManagedSource { get; }
    string DisplayName { get; }

    Task<Result<IReadOnlyList<ManagedInferenceModel>, string>> GetModelsAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<Result<Guid, string>> ImportAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<VoidResult<string>> UpdateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default);
}

public sealed record ManagedInferenceModel(
    string Slug,
    string DisplayName,
    string? DefaultReasoningEffort,
    IReadOnlyList<string> EffortLevels);

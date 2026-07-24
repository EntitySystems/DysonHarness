namespace DysonHarness;

/// <summary>API surface for <see cref="DysonProviderKinds.OpenAICompatible"/> providers.</summary>
public static class DysonOpenAiApiModes
{
    public const string Completions = "Completions";
    public const string Responses = "Responses";

    public static readonly string[] All = [Completions, Responses];

    public static bool IsValid(string? mode) =>
        string.Equals(mode, Completions, StringComparison.Ordinal)
        || string.Equals(mode, Responses, StringComparison.Ordinal);

    public static string Normalize(string? mode) =>
        IsValid(mode) ? mode! : Completions;
}

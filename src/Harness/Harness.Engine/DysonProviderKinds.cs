namespace DysonHarness;

/// <summary>Known provider-kind strings stored on <see cref="DysonModelProviderEntity.ProviderKind"/>.</summary>
public static class DysonProviderKinds
{
    public const string Demo = "demo";
    public const string OpenAICompatible = "OpenAICompatible";
    public const string Anthropic = "Anthropic";

    public static readonly string[] All = [Demo, OpenAICompatible, Anthropic];
}

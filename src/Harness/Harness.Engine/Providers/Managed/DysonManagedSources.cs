namespace DysonHarness;

/// <summary>Known <see cref="DysonModelProviderEntity.ManagedSource"/> values.</summary>
public static class DysonManagedSources
{
    public const string CliProxyCodex = "cliproxy-codex";
    public const string CliProxyGrok = "cliproxy-grok";
    public const string CliProxyAntigravity = "cliproxy-antigravity";
    public const string CliProxyKimi = "cliproxy-kimi";
    public const string CliProxyClaude = "cliproxy-claude";
    public const string OpenRouter = "openrouter";

    public static bool IsCliProxy(string? source) =>
        !string.IsNullOrWhiteSpace(source)
        && source.StartsWith("cliproxy-", StringComparison.Ordinal);

    public static bool IsOpenRouter(string? source) =>
        string.Equals(source, OpenRouter, StringComparison.Ordinal);
}

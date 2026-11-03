// Derived from docs/inference-providers/chatgpt-codex.md (recommended ChatGPT list).
// Live list shape matches OpenAI GET /models. See THIRD-PARTY-NOTICES.md.

using System.Text.Json;

namespace DysonHarness;

/// <summary>One ChatGPT Codex chat model. <paramref name="ThinkingLevels"/> empty = no reasoning effort.</summary>
public sealed record OpenAiCodexModelInfo(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ThinkingLevels);

/// <summary>Bundled chat model table (static fallback) and live <c>/models</c> parsing.</summary>
public static class OpenAiCodexModelCatalog
{
    private static readonly string[] Efforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>Recommended ChatGPT list from <c>docs/inference-providers/chatgpt-codex.md</c>.</summary>
    public static IReadOnlyList<OpenAiCodexModelInfo> Bundled { get; } =
    [
        new("gpt-5.6-sol", "GPT-5.6 Sol", Efforts),
        new("gpt-5.6-terra", "GPT-5.6 Terra", Efforts),
        new("gpt-5.6-luna", "GPT-5.6 Luna", Efforts),
        new("gpt-5.5", "GPT-5.5", Efforts),
        new("gpt-5.4", "GPT-5.4", Efforts),
        new("gpt-5.4-mini", "GPT-5.4 Mini", Efforts),
        new("gpt-5.3-codex-spark", "GPT-5.3 Codex Spark", Efforts),
    ];

    public static OpenAiCodexModelInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var key = id.Trim();
        return Bundled.FirstOrDefault(m => string.Equals(m.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parse an OpenAI-style <c>{"data":[{"id":...}]}</c> list. Known ids take the bundled thinking levels;
    /// unknown live ids get the same effort set (Codex models share it) until the table learns otherwise.
    /// </summary>
    public static Result<IReadOnlyList<OpenAiCodexModelInfo>, string> ParseLiveModels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsError("Empty models response.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsError("Models response has no data array.");
            }

            var list = new List<OpenAiCodexModelInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idEl)
                    || idEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idEl.GetString()?.Trim();
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    continue;

                var known = Find(id);
                list.Add(known ?? new OpenAiCodexModelInfo(id, id, Efforts));
            }

            return list.Count == 0
                ? Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsError("Models response listed no models.")
                : Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsValue(list);
        }
        catch (JsonException)
        {
            return Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsError("Models response is not valid JSON.");
        }
    }

    /// <summary>
    /// Default effort: <c>medium</c> when offered, else <c>high</c>, else the middle level, else none (omit).
    /// GPT-5.5 / 5.6 default to medium (Codex doc), unlike xAI's high-first rule.
    /// </summary>
    public static string? DefaultEffort(OpenAiCodexModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var levels = model.ThinkingLevels.Where(l => l != "none").ToList();
        if (levels.Count == 0)
            return null;
        if (levels.Contains("medium"))
            return "medium";
        if (levels.Contains("high"))
            return "high";
        return levels[levels.Count / 2];
    }

    public static ManagedSlugSpec ToSlugSpec(OpenAiCodexModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ManagedSlugSpec(model.Id, model.DisplayName, DefaultEffort(model), model.ThinkingLevels);
    }
}

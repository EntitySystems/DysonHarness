// Derived from router-for-me/CLIProxyAPI (MIT) internal/registry/models/models.json ("xai" section)
//   @ 97f244b8ddb9cbf564b6e6faab0159102cca8617. See THIRD-PARTY-NOTICES.md.

using System.Text.Json;

namespace DysonHarness;

/// <summary>One xAI chat model. <paramref name="ThinkingLevels"/> empty = the model takes no reasoning effort.</summary>
public sealed record XaiModelInfo(
    string Id,
    string DisplayName,
    int? ContextLength,
    IReadOnlyList<string> ThinkingLevels);

/// <summary>Bundled chat model table (static fallback) and live <c>/models</c> parsing. Chat models only.</summary>
public static class XaiGrokModelCatalog
{
    private static readonly string[] Standard4 = ["low", "medium", "high", "xhigh"];
    private static readonly string[] Standard3 = ["low", "medium", "high"];

    /// <summary>Snapshot of CLIProxy's <c>xai</c> registry section. Refresh by diffing upstream.</summary>
    public static IReadOnlyList<XaiModelInfo> Bundled { get; } =
    [
        new("grok-4.7", "Grok 4.7", 500_000, Standard4),
        new("grok-4.7-build-fast", "Grok 4.7 Fast", 500_000, Standard4),
        new("grok-4.6", "Grok 4.6", 500_000, Standard4),
        new("grok-build-0.1", "Grok Build 0.1", 256_000, []),
        new("grok-4.5", "Grok 4.5", 500_000, Standard3),
        new("grok-4.3", "Grok 4.3", 1_000_000, ["none", "low", "medium", "high"]),
        new("grok-4.20-0309-reasoning", "Grok 4.20 0309 Reasoning", 2_000_000, []),
        new("grok-4.20-0309-non-reasoning", "Grok 4.20 0309 Non Reasoning", 2_000_000, []),
        new("grok-4.20-multi-agent-0309", "Grok 4.20 Multi Agent 0309", 2_000_000, Standard3),
        new("grok-3-mini", "Grok 3 Mini", 131_072, Standard3),
        new("grok-3-mini-fast", "Grok 3 Mini Fast", 131_072, Standard3),
        new("grok-composer-2.5-fast", "Composer 2.5 Fast", 200_000, []),
    ];

    /// <summary>Image/video generation ids are not chat models and are never offered as chat slugs.</summary>
    public static bool IsChatModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && !id.Trim().StartsWith("grok-imagine-", StringComparison.OrdinalIgnoreCase);

    public static XaiModelInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var key = id.Trim();
        return Bundled.FirstOrDefault(m => string.Equals(m.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parse an OpenAI-style <c>{"data":[{"id":...}]}</c> list. Known ids take the bundled thinking levels;
    /// unknown live ids get none (effort omitted) until the bundled table learns them.
    /// </summary>
    public static Result<IReadOnlyList<XaiModelInfo>, string> ParseLiveModels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<IReadOnlyList<XaiModelInfo>, string>.AsError("Empty models response.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return Result<IReadOnlyList<XaiModelInfo>, string>.AsError("Models response has no data array.");
            }

            var list = new List<XaiModelInfo>();
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
                if (!IsChatModelId(id) || !seen.Add(id!))
                    continue;

                var known = Find(id);
                list.Add(known ?? new XaiModelInfo(id!, id!, null, []));
            }

            return list.Count == 0
                ? Result<IReadOnlyList<XaiModelInfo>, string>.AsError("Models response listed no chat models.")
                : Result<IReadOnlyList<XaiModelInfo>, string>.AsValue(list);
        }
        catch (JsonException)
        {
            return Result<IReadOnlyList<XaiModelInfo>, string>.AsError("Models response is not valid JSON.");
        }
    }

    /// <summary>Default effort: <c>high</c> when offered, else the middle level, else none (omit).</summary>
    public static string? DefaultEffort(XaiModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var levels = model.ThinkingLevels.Where(l => l != "none").ToList();
        if (levels.Count == 0)
            return null;

        return levels.Contains("high") ? "high" : levels[levels.Count / 2];
    }

    public static ManagedSlugSpec ToSlugSpec(XaiModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ManagedSlugSpec(model.Id, model.DisplayName, DefaultEffort(model), model.ThinkingLevels);
    }

    /// <summary>
    /// Map a requested effort onto the model's real levels (Dyson's defaults offer none/minimal/…/xhigh).
    /// Returns <c>null</c> to omit. <c>none</c> is only kept where the model allows it.
    /// </summary>
    public static string? ClampEffort(string? requested, IReadOnlyList<string> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var effort = requested?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(effort) || levels.Count == 0)
            return null;
        if (levels.Contains(effort))
            return effort;
        if (effort == "none")
            return null;

        string[] order = ["minimal", "low", "medium", "high", "xhigh"];
        var want = Array.IndexOf(order, effort);
        if (want < 0)
            return null;

        // Nearest offered level; ties go to the lower one.
        return levels
            .Select(l => (Level: l, Index: Array.IndexOf(order, l)))
            .Where(x => x.Index >= 0)
            .OrderBy(x => Math.Abs(x.Index - want))
            .ThenBy(x => x.Index)
            .Select(x => x.Level)
            .FirstOrDefault();
    }
}

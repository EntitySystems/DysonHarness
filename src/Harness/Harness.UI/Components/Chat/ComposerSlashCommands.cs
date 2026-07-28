using DysonHarness;

namespace Harness.UI.Components.Chat;

/// <summary>
/// Leading <c>/</c> command parsing for the composer overlay (modes, new session, model switch, skills).
/// </summary>
public static class ComposerSlashCommands
{
    public const int MaxSuggestions = 5;

    public enum Kind
    {
        Mode,
        NewSession,
        Model,
        Skill,
    }

    public sealed record ModelOption(
        Guid Id,
        string DisplayAlias,
        string ProviderName,
        string Slug,
        string Label);

    public sealed record SkillOption(
        string Name,
        string DisplayName,
        string Token,
        string Label);

    public sealed record Suggestion(
        string Token,
        string Label,
        Kind Kind,
        string? Mode = null,
        Guid? ModelSlugId = null,
        string? SkillName = null);

    public sealed record ParseResult(
        Suggestion Suggestion,
        string Remainder);

    private static readonly Suggestion[] BuiltIns =
    [
        new("/ask", "Ask mode", Kind.Mode, Mode: DysonAgentModes.Ask),
        new("/plan", "Plan mode", Kind.Mode, Mode: DysonAgentModes.Plan),
        new("/work", "Work mode", Kind.Mode, Mode: DysonAgentModes.Work),
        new("/new", "New session", Kind.NewSession),
    ];

    /// <summary>
    /// Active overlay token: entire text is a leading <c>/</c> token with no trailing content.
    /// </summary>
    public static bool TryGetActiveToken(string? text, out string token)
    {
        token = "";
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return false;

        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
                return false;
        }

        token = text;
        return true;
    }

    /// <summary>
    /// Leading <c>/</c> token for send-time apply (allows trailing remainder).
    /// </summary>
    public static bool TryGetLeadingToken(string? text, out string token, out string remainder)
    {
        token = "";
        remainder = text ?? "";
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return false;

        var end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        token = text[..end];
        remainder = text[end..].TrimStart();
        return true;
    }

    public static IReadOnlyList<Suggestion> Filter(
        string typedToken,
        IReadOnlyList<ModelOption> models,
        IReadOnlyList<SkillOption>? skills = null)
    {
        var filter = typedToken.StartsWith('/') ? typedToken[1..] : typedToken;
        filter = filter.Trim();

        if (IsSkillFilter(filter))
            return FilterSkills(filter, skills ?? []);

        var results = new List<Suggestion>(MaxSuggestions);

        foreach (var builtIn in BuiltIns)
        {
            if (results.Count >= MaxSuggestions)
                break;
            if (MatchesBuiltIn(builtIn.Token, filter))
                results.Add(builtIn);
        }

        if (results.Count >= MaxSuggestions)
            return results;

        var ranked = RankModels(filter, models);
        foreach (var model in ranked)
        {
            if (results.Count >= MaxSuggestions)
                break;
            results.Add(new Suggestion(
                "/" + model.Slug,
                $"{model.DisplayAlias} · {model.ProviderName}",
                Kind.Model,
                ModelSlugId: model.Id));
        }

        return results;
    }

    /// <summary>
    /// Resolve a leading command for send: exact built-in/display-alias/skill, else first filtered suggestion.
    /// </summary>
    public static bool TryResolve(
        string? text,
        IReadOnlyList<ModelOption> models,
        out ParseResult? result,
        IReadOnlyList<SkillOption>? skills = null)
    {
        result = null;
        if (!TryGetLeadingToken(text, out var token, out var remainder))
            return false;

        var name = token.StartsWith('/') ? token[1..] : token;
        skills ??= [];

        if (IsSkillFilter(name))
        {
            var skillSuggestions = FilterSkills(name, skills);
            if (skillSuggestions.Count == 0)
                return false;

            result = new ParseResult(skillSuggestions[0], remainder);
            return true;
        }

        var builtIn = BuiltIns.FirstOrDefault(b =>
            string.Equals(b.Token, token, StringComparison.OrdinalIgnoreCase));
        if (builtIn is not null)
        {
            result = new ParseResult(builtIn, remainder);
            return true;
        }

        var modelExact = models.FirstOrDefault(m =>
            string.Equals(m.DisplayAlias, name, StringComparison.OrdinalIgnoreCase));
        if (modelExact is not null)
        {
            result = new ParseResult(
                new Suggestion(
                    "/" + modelExact.Slug,
                    $"{modelExact.DisplayAlias} · {modelExact.ProviderName}",
                    Kind.Model,
                    ModelSlugId: modelExact.Id),
                remainder);
            return true;
        }

        // Exact skill token /skill-{name} even when filter prefix is not "skill"
        var skillExact = skills.FirstOrDefault(s =>
            string.Equals(s.Token, token, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (skillExact is not null)
        {
            result = new ParseResult(
                new Suggestion(
                    skillExact.Token,
                    skillExact.Label,
                    Kind.Skill,
                    SkillName: skillExact.Name),
                remainder);
            return true;
        }

        // ponytail: ceiling = first filtered hit; upgrade if send should require an exact token.
        var suggestions = Filter(token, models, skills);
        if (suggestions.Count == 0)
            return false;

        result = new ParseResult(suggestions[0], remainder);
        return true;
    }

    public static SkillOption ToSkillOption(DysonSkillCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var token = "/skill-" + SanitizeSkillToken(entry.Name);
        return new SkillOption(
            entry.Name,
            entry.DisplayName,
            token,
            $"{entry.DisplayName} · skill");
    }

    private static bool IsSkillFilter(string filter) =>
        filter.StartsWith("skill", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<Suggestion> FilterSkills(
        string filter,
        IReadOnlyList<SkillOption> skills)
    {
        if (skills.Count == 0)
            return [];

        // Strip "skill" / "skill-" prefix for ranking the skill id/display.
        var needle = filter;
        if (needle.StartsWith("skill-", StringComparison.OrdinalIgnoreCase))
            needle = needle["skill-".Length..];
        else if (needle.StartsWith("skill", StringComparison.OrdinalIgnoreCase))
            needle = needle["skill".Length..].TrimStart('-');

        var ranked = skills
            .Select(s => (Skill: s, Score: ScoreSkill(s, needle)))
            .Where(x => needle.Length == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Skill.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(x => new Suggestion(
                x.Skill.Token,
                x.Skill.Label,
                Kind.Skill,
                SkillName: x.Skill.Name))
            .ToArray();

        return ranked;
    }

    private static int ScoreSkill(SkillOption skill, string needle)
    {
        if (needle.Length == 0)
            return 1;
        if (string.Equals(skill.Name, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(skill.DisplayName, needle, StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        if (skill.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
            || skill.DisplayName.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        if (needle.Length >= 2
            && (skill.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || skill.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)))
        {
            return 100;
        }

        return 0;
    }

    private static string SanitizeSkillToken(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '/' or '\\' or '.')
                sb.Append('-');
        }

        var token = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(token) ? "skill" : token;
    }

    private static bool MatchesBuiltIn(string commandToken, string filter)
    {
        if (filter.Length == 0)
            return true;
        var name = commandToken.StartsWith('/') ? commandToken[1..] : commandToken;
        return name.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ModelOption> RankModels(string filter, IReadOnlyList<ModelOption> models)
    {
        if (models.Count == 0)
            return [];

        if (filter.Length == 0)
        {
            return models
                .OrderBy(m => m.ProviderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.DisplayAlias, StringComparer.OrdinalIgnoreCase);
        }

        return models
            .Select(m => (Model: m, Score: ScoreModel(m, filter)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Model.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model.DisplayAlias, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Model);
    }

    private static int ScoreModel(ModelOption m, string filter)
    {
        if (string.Equals(m.DisplayAlias, filter, StringComparison.OrdinalIgnoreCase))
            return 300;
        if (m.DisplayAlias.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            return 200;
        // ponytail: ceiling = contains only for 3+ chars (avoids /as matching "fast").
        // Do not match Label — it embeds " / {slug}", which would accept raw slug-only input.
        if (filter.Length >= 3
            && (m.DisplayAlias.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || m.ProviderName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            return 100;
            return 0;
    }
}

using DysonHarness;

namespace Harness.UI.Components.Chat;

/// <summary>
/// Leading <c>/</c> command parsing for the composer overlay (modes, new session, model switch).
/// </summary>
public static class ComposerSlashCommands
{
    public const int MaxSuggestions = 5;

    public enum Kind
    {
        Mode,
        NewSession,
        Model,
    }

    public sealed record ModelOption(
        Guid Id,
        string DisplayAlias,
        string ProviderName,
        string Slug,
        string Label);

    public sealed record Suggestion(
        string Token,
        string Label,
        Kind Kind,
        string? Mode = null,
        Guid? ModelSlugId = null);

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

    public static IReadOnlyList<Suggestion> Filter(string typedToken, IReadOnlyList<ModelOption> models)
    {
        var filter = typedToken.StartsWith('/') ? typedToken[1..] : typedToken;
        filter = filter.Trim();

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
    /// Resolve a leading command for send: exact built-in/display-alias, else first filtered suggestion.
    /// </summary>
    public static bool TryResolve(string? text, IReadOnlyList<ModelOption> models, out ParseResult? result)
    {
        result = null;
        if (!TryGetLeadingToken(text, out var token, out var remainder))
            return false;

        var name = token.StartsWith('/') ? token[1..] : token;

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

        // ponytail: ceiling = first filtered hit; upgrade if send should require an exact token.
        var suggestions = Filter(token, models);
        if (suggestions.Count == 0)
            return false;

        result = new ParseResult(suggestions[0], remainder);
        return true;
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

    /// <summary>ponytail: assert-only self-check (no test framework). Run from UI <c>Program</c>.</summary>
    public static void SelfCheck()
    {
        if (!TryGetActiveToken("/ask", out var active) || active != "/ask")
            throw new InvalidOperationException("Active token /ask failed.");
        if (TryGetActiveToken("/ask hello", out _))
            throw new InvalidOperationException("Active token should reject trailing text.");
        if (!TryGetLeadingToken("/ask  hello", out var lead, out var rest)
            || lead != "/ask"
            || rest != "hello")
            throw new InvalidOperationException("Leading token parse failed.");

        var models = new List<ModelOption>
        {
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "GPT Fast", "OpenAI", "gpt-fast", "GPT Fast · OpenAI / gpt-fast"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Claude", "Anthropic", "claude-sonnet", "Claude · Anthropic / claude-sonnet"),
        };

        var atRoot = Filter("/", models);
        if (atRoot.Count == 0 || atRoot.Count > MaxSuggestions)
            throw new InvalidOperationException("Filter(/) count out of range.");
        if (atRoot[0].Token != "/ask")
            throw new InvalidOperationException("Expected /ask first for empty filter.");

        var ask = Filter("/as", models);
        if (ask.Count != 1 || ask[0].Mode != DysonAgentModes.Ask)
            throw new InvalidOperationException("Filter(/as) should yield /ask only.");

        var gpt = Filter("/gpt", models);
        if (gpt.Count != 1 || gpt[0].ModelSlugId != models[0].Id)
            throw new InvalidOperationException("Filter(/gpt) should match GPT Fast.");

        if (!TryResolve("/new do stuff", models, out var parsed)
            || parsed!.Suggestion.Kind != Kind.NewSession
            || parsed.Remainder != "do stuff")
            throw new InvalidOperationException("TryResolve(/new) failed.");

        if (!TryResolve("/GPT Fast", models, out var modelParsed)
            || modelParsed!.Suggestion.ModelSlugId != models[0].Id)
            throw new InvalidOperationException("TryResolve display alias failed.");

        if (TryResolve("/gpt-fast", models, out _))
            throw new InvalidOperationException("TryResolve should reject raw slug-only input.");

        if (TryResolve("/zzzz-nope", models, out _))
            throw new InvalidOperationException("TryResolve should reject unknown token.");

        // Provider tie-break: same alias across providers should order alphabetically by provider name.
        var duplicateAliasModels = new List<ModelOption>
        {
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "GPT-4o", "Beta Provider", "gpt-4o-beta", "GPT-4o · Beta Provider / gpt-4o-beta"),
            new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "GPT-4o", "Alpha Provider", "gpt-4o-alpha", "GPT-4o · Alpha Provider / gpt-4o-alpha"),
        };
        var duplicateResults = Filter("/gpt-4o", duplicateAliasModels);
        if (duplicateResults.Count != 2)
            throw new InvalidOperationException("Filter(/gpt-4o) should return both duplicate-alias models.");
        if (duplicateResults[0].Label != "GPT-4o · Alpha Provider")
            throw new InvalidOperationException("Filter should order duplicate aliases by provider name.");
    }
}

using Harness.UI.Components.Chat;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Composer slash-command parse / filter (Xunit).</summary>
public class ComposerSlashCommandsTests
{
    [Fact]
    public void Run()
    {
        if (!ComposerSlashCommands.TryGetActiveToken("/ask", out var active) || active != "/ask")
            throw new InvalidOperationException("Active token /ask failed.");
        if (ComposerSlashCommands.TryGetActiveToken("/ask hello", out _))
            throw new InvalidOperationException("Active token should reject trailing text.");
        if (!ComposerSlashCommands.TryGetLeadingToken("/ask  hello", out var lead, out var rest)
            || lead != "/ask"
            || rest != "hello")
            throw new InvalidOperationException("Leading token parse failed.");

        var models = new List<ComposerSlashCommands.ModelOption>
        {
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "GPT Fast", "OpenAI", "gpt-fast", "GPT Fast · OpenAI / gpt-fast"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Claude", "Anthropic", "claude-sonnet", "Claude · Anthropic / claude-sonnet"),
        };

        var atRoot = ComposerSlashCommands.Filter("/", models);
        if (atRoot.Count == 0 || atRoot.Count > ComposerSlashCommands.MaxSuggestions)
            throw new InvalidOperationException("Filter(/) count out of range.");
        if (atRoot[0].Token != "/ask")
            throw new InvalidOperationException("Expected /ask first for empty filter.");

        var ask = ComposerSlashCommands.Filter("/as", models);
        if (ask.Count != 1 || ask[0].Mode != DysonAgentModes.Ask)
            throw new InvalidOperationException("Filter(/as) should yield /ask only.");

        var gpt = ComposerSlashCommands.Filter("/gpt", models);
        if (gpt.Count != 1 || gpt[0].ModelSlugId != models[0].Id)
            throw new InvalidOperationException("Filter(/gpt) should match GPT Fast.");

        if (!ComposerSlashCommands.TryResolve("/new do stuff", models, out var parsed)
            || parsed!.Suggestion.Kind != ComposerSlashCommands.Kind.NewSession
            || parsed.Remainder != "do stuff")
            throw new InvalidOperationException("TryResolve(/new) failed.");

        if (!ComposerSlashCommands.TryResolve("/GPT Fast", models, out var modelParsed)
            || modelParsed!.Suggestion.ModelSlugId != models[0].Id)
            throw new InvalidOperationException("TryResolve display alias failed.");

        if (ComposerSlashCommands.TryResolve("/gpt-fast", models, out _))
            throw new InvalidOperationException("TryResolve should reject raw slug-only input.");

        if (ComposerSlashCommands.TryResolve("/zzzz-nope", models, out _))
            throw new InvalidOperationException("TryResolve should reject unknown token.");

        var duplicateAliasModels = new List<ComposerSlashCommands.ModelOption>
        {
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "GPT-4o", "Beta Provider", "gpt-4o-beta", "GPT-4o · Beta Provider / gpt-4o-beta"),
            new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "GPT-4o", "Alpha Provider", "gpt-4o-alpha", "GPT-4o · Alpha Provider / gpt-4o-alpha"),
        };
        var duplicateResults = ComposerSlashCommands.Filter("/gpt-4o", duplicateAliasModels);
        if (duplicateResults.Count != 2)
            throw new InvalidOperationException("Filter(/gpt-4o) should return both duplicate-alias models.");
        if (duplicateResults[0].Label != "GPT-4o · Alpha Provider")
            throw new InvalidOperationException("Filter should order duplicate aliases by provider name.");

        var skills = new List<ComposerSlashCommands.SkillOption>
        {
            ComposerSlashCommands.ToSkillOption(
                new DysonSkillCatalogEntry("JDSL", "JDSL", DysonSkillSource.Included)),
            ComposerSlashCommands.ToSkillOption(
                new DysonSkillCatalogEntry("angular-skill", "angular-skill", DysonSkillSource.DysonSkills)),
            ComposerSlashCommands.ToSkillOption(
                new DysonSkillCatalogEntry("openrules", "OpenRules skill", DysonSkillSource.OpenRules)),
        };

        var openrulesOpt = skills[^1];
        if (openrulesOpt.Token != "/skill-openrules" || openrulesOpt.Name != "openrules")
        {
            throw new InvalidOperationException(
                "ToSkillOption short Name must yield /skill-openrules (not a URL slug).");
        }

        var skillRoot = ComposerSlashCommands.Filter("/skill", models, skills);
        if (skillRoot.Count == 0
            || skillRoot.Any(s => s.Kind != ComposerSlashCommands.Kind.Skill)
            || skillRoot.Any(s => s.Kind == ComposerSlashCommands.Kind.Model))
        {
            throw new InvalidOperationException("Filter(/skill) must return skills only, not models.");
        }

        var jdsl = ComposerSlashCommands.Filter("/skill-jdsl", models, skills);
        if (jdsl.Count != 1
            || jdsl[0].Kind != ComposerSlashCommands.Kind.Skill
            || jdsl[0].SkillName != "JDSL"
            || jdsl[0].Token != "/skill-jdsl")
        {
            throw new InvalidOperationException("Filter(/skill-jdsl) should resolve JDSL skill.");
        }

        if (!ComposerSlashCommands.TryResolve("/skill-jdsl do X", models, out var skillParsed, skills)
            || skillParsed!.Suggestion.Kind != ComposerSlashCommands.Kind.Skill
            || skillParsed.Suggestion.SkillName != "JDSL"
            || skillParsed.Remainder != "do X")
        {
            throw new InvalidOperationException("TryResolve(/skill-jdsl do X) failed.");
        }

        if (!ComposerSlashCommands.TryResolve("/skill-jdsl", models, out var skillOnly, skills)
            || skillOnly!.Suggestion.Kind != ComposerSlashCommands.Kind.Skill
            || skillOnly.Remainder != "")
        {
            throw new InvalidOperationException("TryResolve(/skill-jdsl) apply-only remainder failed.");
        }

        var skillSearch = ComposerSlashCommands.Filter("/skill-s", models, skills);
        if (skillSearch.Count != 1
            || skillSearch[0].Kind != ComposerSlashCommands.Kind.SkillSearch
            || skillSearch[0].Token != "/skill-search")
        {
            throw new InvalidOperationException("Filter(/skill-s) should yield /skill-search before skills.");
        }

        var skillSearchExact = ComposerSlashCommands.Filter("/skill-search", models, skills);
        if (skillSearchExact.Count != 1
            || skillSearchExact[0].Kind != ComposerSlashCommands.Kind.SkillSearch)
        {
            throw new InvalidOperationException("Filter(/skill-search) should yield SkillSearch only.");
        }

        var skillSearchCase = ComposerSlashCommands.Filter("/SKILL-SE", models, skills);
        if (skillSearchCase.Count != 1
            || skillSearchCase[0].Kind != ComposerSlashCommands.Kind.SkillSearch)
        {
            throw new InvalidOperationException("Filter(/SKILL-SE) should match SkillSearch case-insensitively.");
        }

        if (!ComposerSlashCommands.TryResolve("/skill-search", models, out var searchParsed, skills)
            || searchParsed!.Suggestion.Kind != ComposerSlashCommands.Kind.SkillSearch
            || searchParsed.Remainder != "")
        {
            throw new InvalidOperationException("TryResolve(/skill-search) failed.");
        }

        if (!ComposerSlashCommands.TryResolve("/skill-search then install", models, out var searchRemainder, skills)
            || searchRemainder!.Suggestion.Kind != ComposerSlashCommands.Kind.SkillSearch
            || searchRemainder.Remainder != "then install")
        {
            throw new InvalidOperationException("TryResolve(/skill-search then install) remainder failed.");
        }

        if (atRoot.All(s => s.Kind != ComposerSlashCommands.Kind.SkillSearch))
        {
            throw new InvalidOperationException("Filter(/) should include /skill-search among built-ins.");
        }

        var stillSkills = ComposerSlashCommands.Filter("/skill", models, skills);
        if (stillSkills.Count == 0
            || stillSkills.Any(s => s.Kind != ComposerSlashCommands.Kind.Skill))
        {
            throw new InvalidOperationException("Filter(/skill) must still list local skills, not /skill-search.");
        }

        if (!ComposerSlashCommands.TryResolve("/help", models, out var helpParsed, skills)
            || helpParsed!.Suggestion.Kind != ComposerSlashCommands.Kind.Help
            || helpParsed.Remainder != "")
        {
            throw new InvalidOperationException("TryResolve(/help) failed.");
        }

        if (!ComposerSlashCommands.TryResolve("/help then docs", models, out var helpRemainder, skills)
            || helpRemainder!.Suggestion.Kind != ComposerSlashCommands.Kind.Help
            || helpRemainder.Remainder != "then docs")
        {
            throw new InvalidOperationException("TryResolve(/help then docs) remainder failed.");
        }

        var helpFilter = ComposerSlashCommands.Filter("/h", models, skills);
        if (helpFilter.All(s => s.Kind != ComposerSlashCommands.Kind.Help)
            || helpFilter.All(s => s.Token != "/help"))
        {
            throw new InvalidOperationException("Filter(/h) should include /help.");
        }

        var helpExact = ComposerSlashCommands.Filter("/help", models, skills);
        if (helpExact.Count == 0
            || helpExact[0].Kind != ComposerSlashCommands.Kind.Help
            || helpExact[0].Token != "/help")
        {
            throw new InvalidOperationException("Filter(/help) should yield Help.");
        }

        if (ComposerSlashCommands.HelpCatalog.Count == 0
            || ComposerSlashCommands.HelpCatalog.All(e => e.Template != "/help")
            || ComposerSlashCommands.HelpCatalog.All(e => e.Section != ComposerSlashCommands.HelpSection.Pattern))
        {
            throw new InvalidOperationException("HelpCatalog must include /help and pattern rows.");
        }

        if (!ComposerSlashCommands.IsSkillCatalogToken("/skill")
            || !ComposerSlashCommands.IsSkillCatalogToken("/skill-")
            || !ComposerSlashCommands.IsSkillCatalogToken("/skill-foo")
            || !ComposerSlashCommands.IsSkillCatalogToken("/skill-search")
            || !ComposerSlashCommands.IsSkillCatalogToken("skill-bar")
            || ComposerSlashCommands.IsSkillCatalogToken("/ask")
            || ComposerSlashCommands.IsSkillCatalogToken("/help")
            || ComposerSlashCommands.IsSkillCatalogToken("/"))
        {
            throw new InvalidOperationException("IsSkillCatalogToken should match /skill… only.");
        }
    }
}

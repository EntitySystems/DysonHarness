using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert Explore / Security Review / Bug Review settings-default resolve order
/// (explicit modelSlug wins; blank + config default uses default; blank + no default inherits).
/// </summary>
public class DysonSubagentModelDefaultTests
{
    [Fact]
    public void Run()
    {
        AssertBuiltInPromptsContainModelSlugOmissionGuidance();

        var explore = new OpenAiCompatibleAgentProvider(
            new DysonModelSlugEntity { Slug = "explore-default", DisplayAlias = "Explore" });
        var security = new OpenAiCompatibleAgentProvider(
            new DysonModelSlugEntity { Slug = "security-default", DisplayAlias = "Security" });
        var bug = new OpenAiCompatibleAgentProvider(
            new DysonModelSlugEntity { Slug = "bug-default", DisplayAlias = "Bug" });

        var config = new DysonAgentSessionConfig
        {
            ExploreDefaultProvider = explore,
            SecurityReviewDefaultProvider = security,
            BugReviewDefaultProvider = bug,
        };

        if (!ReferenceEquals(config.TryGetSubagentDefaultProvider(DysonAgentModes.Explore), explore))
            throw new InvalidOperationException("Explore should map to ExploreDefaultProvider.");
        if (!ReferenceEquals(config.TryGetSubagentDefaultProvider(DysonAgentModes.SecurityReview), security))
            throw new InvalidOperationException("Security Review should map to SecurityReviewDefaultProvider.");
        if (!ReferenceEquals(config.TryGetSubagentDefaultProvider(DysonAgentModes.BugReview), bug))
            throw new InvalidOperationException("Bug Review should map to BugReviewDefaultProvider.");

        if (config.TryGetSubagentDefaultProvider("explore") is null)
            throw new InvalidOperationException("Mode lookup should be case-insensitive.");

        if (config.TryGetSubagentDefaultProvider(DysonAgentModes.Work) is not null
            || config.TryGetSubagentDefaultProvider(DysonAgentModes.Drone) is not null
            || config.TryGetSubagentDefaultProvider(DysonAgentModes.Ask) is not null)
        {
            throw new InvalidOperationException("Non-override modes must inherit (no settings default).");
        }

        // Resolve order: explicit modelSlug wins (settings default ignored).
        if (config.TryGetSubagentDefaultWhenSlugOmitted("gpt-4o", DysonAgentModes.Explore) is not null)
            throw new InvalidOperationException("Explicit modelSlug must win over settings default.");

        // Blank + config default → use default.
        if (!ReferenceEquals(
                config.TryGetSubagentDefaultWhenSlugOmitted(null, DysonAgentModes.Explore),
                explore)
            || !ReferenceEquals(
                config.TryGetSubagentDefaultWhenSlugOmitted("  ", DysonAgentModes.SecurityReview),
                security)
            || !ReferenceEquals(
                config.TryGetSubagentDefaultWhenSlugOmitted(null, DysonAgentModes.BugReview),
                bug))
        {
            throw new InvalidOperationException("Blank modelSlug should use the mode settings default.");
        }

        // Blank + no default → inherit (null).
        if (config.TryGetSubagentDefaultWhenSlugOmitted(null, DysonAgentModes.Work) is not null
            || config.TryGetSubagentDefaultWhenSlugOmitted(null, DysonAgentModes.Drone) is not null)
        {
            throw new InvalidOperationException("Blank modelSlug with no mode default must inherit parent.");
        }

        var empty = new DysonAgentSessionConfig();
        if (empty.TryGetSubagentDefaultWhenSlugOmitted(null, DysonAgentModes.Explore) is not null)
            throw new InvalidOperationException("Unset Explore default must inherit parent.");
    }

    private static void AssertBuiltInPromptsContainModelSlugOmissionGuidance()
    {
        AssertModelSlugOmissionGuidance(DysonAgentSystemPrompts.SharedPreamble, "Shared preamble");

        foreach (var mode in DysonAgentModes.BuiltIns)
        {
            var prompt = DysonAgentSystemPrompts.ForMode(mode);
            if (prompt.IsError)
                throw new InvalidOperationException($"Built-in mode '{mode}' should resolve: {prompt.Error}");

            AssertModelSlugOmissionGuidance(prompt.Value, $"Built-in mode '{mode}' prompt");
        }
    }

    private static void AssertModelSlugOmissionGuidance(string prompt, string subject)
    {
        if (!prompt.Contains("StartSubagent.modelSlug", StringComparison.Ordinal)
            || !prompt.Contains("omit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{subject} should instruct agents to omit StartSubagent.modelSlug unless explicitly requested.");
        }
    }
}

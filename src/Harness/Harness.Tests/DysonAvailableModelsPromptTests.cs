using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert available-models system-prompt catalog filters by kind and formats effort/modes.
/// /// </summary>
public class DysonAvailableModelsPromptTests
{
    [Fact]
    public void Run()
    {
        var providers = new List<DysonModelProviderEntity>
        {
            new()
            {
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                DisplayName = "OAI",
                Slugs =
                [
                    new DysonModelSlugEntity
                    {
                        Slug = "gpt-4o",
                        DisplayAlias = "GPT-4o Fast",
                        DefaultReasoningEffort = "high",
                        ReasoningModes = ["low", "high"],
                    },
                    new DysonModelSlugEntity
                    {
                        Slug = "o1",
                        DisplayAlias = "O1",
                        DefaultReasoningEffort = null,
                        ReasoningModes = [],
                    },
                ],
            },
            new()
            {
                ProviderKind = DysonProviderKinds.Demo,
                DisplayName = "Demo",
                Slugs =
                [
                    new DysonModelSlugEntity
                    {
                        Slug = "demo-mock",
                        DisplayAlias = "Demo Mock",
                        DefaultReasoningEffort = "medium",
                        ReasoningModes = ["medium"],
                    },
                ],
            },
        };

        var block = DysonAgentSystemPrompts.FormatAvailableModelsBlock(
            providers, DysonProviderKinds.OpenAICompatible);
        if (block is null)
            throw new InvalidOperationException("Expected OpenAI-compatible models block.");

        if (!block.Contains("GPT-4o Fast (`gpt-4o`) defaultEffort: high; modes: [low, high]", StringComparison.Ordinal)
            || !block.Contains("O1 (`o1`) defaultEffort: (omit); modes: []", StringComparison.Ordinal)
            || block.Contains("demo-mock", StringComparison.Ordinal)
            || !block.Contains("StartSubagent.modelSlug", StringComparison.Ordinal)
            || !block.Contains("StartSubagent.reasoningEffort", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected models block:\n{block}");
        }

        if (DysonAgentSystemPrompts.FormatAvailableModelsBlock(providers, DysonProviderKinds.Anthropic) is not null)
            throw new InvalidOperationException("Expected null block when no slugs match kind.");

        if (DysonAgentSystemPrompts.BuildAvailableModelsBlockAsync(null, DysonProviderKinds.Demo)
                .GetAwaiter().GetResult() is not null)
        {
            throw new InvalidOperationException("Null model store should skip the models block.");
        }
    }
}

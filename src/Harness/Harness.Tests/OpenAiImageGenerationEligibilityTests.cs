using DysonHarness;

namespace Harness.Tests;

public class OpenAiImageGenerationEligibilityTests
{
    [Fact]
    public void IsEligible_accepts_enabled_direct_openai_v1_slug_only()
    {
        var directProvider = Provider();
        var directSlug = Slug();

        Assert.True(OpenAiImageGenerationEligibility.IsEligible(directSlug, directProvider));
        Assert.True(OpenAiImageGenerationEligibility.IsEligible(directSlug, Provider(baseUrl: null)));
        Assert.False(OpenAiImageGenerationEligibility.IsEligible(directSlug, Provider(baseUrl: "https://example.invalid/v1")));
        Assert.False(OpenAiImageGenerationEligibility.IsEligible(directSlug, Provider(managedSource: DysonManagedSources.OpenRouter)));
        Assert.False(OpenAiImageGenerationEligibility.IsEligible(directSlug, Provider(apiKey: " ")));
        Assert.False(OpenAiImageGenerationEligibility.IsEligible(directSlug, Provider(kind: DysonProviderKinds.Anthropic)));
        Assert.False(OpenAiImageGenerationEligibility.IsEligible(Slug(isEnabled: false), directProvider));
    }

    private static DysonModelProviderEntity Provider(
        string? baseUrl = "https://api.openai.com/v1",
        string? apiKey = "sk-test",
        string kind = DysonProviderKinds.OpenAICompatible,
        string? managedSource = null) =>
        new()
        {
            ProviderKind = kind,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            ManagedSource = managedSource,
        };

    private static DysonModelSlugEntity Slug(bool isEnabled = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            Slug = "gpt-image-1",
            DisplayAlias = "Image",
            IsEnabled = isEnabled,
        };
}

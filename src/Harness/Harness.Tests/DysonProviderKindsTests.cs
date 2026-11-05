using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: IsRealProvider is EffectiveKind != demo — credentialed demo counts, bare demo and empty kind do not.
/// </summary>
public class DysonProviderKindsTests
{
    [Fact]
    public void BareDemo_IsNotReal()
    {
        Assert.False(DysonProviderKinds.IsRealProvider(new DysonModelProviderEntity
        {
            ProviderKind = DysonProviderKinds.Demo,
        }));
    }

    [Fact]
    public void DemoWithBaseUrl_IsReal()
    {
        Assert.True(DysonProviderKinds.IsRealProvider(new DysonModelProviderEntity
        {
            ProviderKind = DysonProviderKinds.Demo,
            BaseUrl = "https://example.test/v1",
        }));
    }

    [Fact]
    public void DemoWithApiKey_IsReal()
    {
        Assert.True(DysonProviderKinds.IsRealProvider(new DysonModelProviderEntity
        {
            ProviderKind = DysonProviderKinds.Demo,
            ApiKey = "sk-test",
        }));
    }

    [Fact]
    public void OpenAICompatible_IsReal()
    {
        Assert.True(DysonProviderKinds.IsRealProvider(new DysonModelProviderEntity
        {
            ProviderKind = DysonProviderKinds.OpenAICompatible,
        }));
    }

    [Fact]
    public void Anthropic_IsReal()
    {
        Assert.True(DysonProviderKinds.IsRealProvider(new DysonModelProviderEntity
        {
            ProviderKind = DysonProviderKinds.Anthropic,
        }));
    }

    [Fact]
    public void EmptyKindWithoutCredentials_IsNotReal()
    {
        Assert.False(DysonProviderKinds.IsRealProvider(new DysonModelProviderEntity
        {
            ProviderKind = "  ",
        }));
    }
}

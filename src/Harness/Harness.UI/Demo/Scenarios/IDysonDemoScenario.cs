#if DYSON_PREVIEW
using DysonHarness;

namespace Harness.UI.Demo;

public interface IDysonDemoScenario
{
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    Task<VoidResult<string>> RunAsync(IDysonDemoHooks hooks, CancellationToken ct);
}

public static class DysonDemoScenarios
{
    public static IReadOnlyList<IDysonDemoScenario> All { get; } = [new AesterShardingDemoScenario()];
}
#endif

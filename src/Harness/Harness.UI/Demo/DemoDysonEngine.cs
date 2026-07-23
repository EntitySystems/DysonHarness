using DysonHarness;

namespace Harness.UI.Demo;

public sealed class DemoDysonEngine : DysonEngine
{
    public DemoDysonEngine(DemoDysonAgentSession rootSession)
    {
        RootSession = rootSession ?? throw new ArgumentNullException(nameof(rootSession));
    }

    public override DysonAgentSession RootSession { get; }

    public DemoDysonAgentSession DemoRoot => (DemoDysonAgentSession)RootSession;
}

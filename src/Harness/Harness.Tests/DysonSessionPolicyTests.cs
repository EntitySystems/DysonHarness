using DysonHarness;

namespace Harness.Tests;

public class DysonSessionPolicyTests
{
    [Fact]
    public void MetaAgentMode_Lockstep_DoesNotMatchDroneOrWork()
    {
        Assert.Equal(DysonSessionPolicy.MetaAgentMode, DysonAgentModes.MetaAgent);
        Assert.True(DysonSessionPolicy.IsMetaAgent("meta agent"));
        Assert.False(DysonSessionPolicy.IsMetaAgent(DysonAgentModes.MetaAgentDrone));
        Assert.False(DysonSessionPolicy.IsMetaAgent(DysonAgentModes.Work));
        Assert.False(DysonSessionPolicy.IsMetaAgent(null));
        Assert.False(DysonSessionPolicy.IsMetaAgent(""));
    }
}

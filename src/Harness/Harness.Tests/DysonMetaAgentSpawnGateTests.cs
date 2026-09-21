using DysonHarness;

namespace Harness.Tests;

/// <summary>Soft spawn matrix for Meta Agent / Meta Agent Drone (Xunit Fact).</summary>
public class DysonMetaAgentSpawnGateTests
{
    [Fact]
    public void MetaAgent_may_spawn_meta_agent_drone_or_explore()
    {
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgent, DysonAgentModes.MetaAgentDrone));
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgent, DysonAgentModes.Explore));
    }

    [Fact]
    public void MetaAgent_rejects_classic_drone_and_plan()
    {
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgent, DysonAgentModes.Drone),
            "Meta Agent may only spawn Meta Agent Drone or Explore subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgent, DysonAgentModes.Plan),
            "Plan cannot be used as a subagent mode (top-level only).");
    }

    [Fact]
    public void Work_rejects_meta_agent_drone()
    {
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Work, DysonAgentModes.MetaAgentDrone),
            "Meta Agent Drone may only be spawned by a Meta Agent session.");
    }

    [Fact]
    public void MetaAgentDrone_may_spawn_classic_drone_or_explore_but_not_another_meta_agent_drone()
    {
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgentDrone, DysonAgentModes.Drone));
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgentDrone, DysonAgentModes.Explore));
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgentDrone, DysonAgentModes.MetaAgentDrone),
            "No multi-layer Meta Agent Drones; spawn a Drone or Explore.");
    }

    [Fact]
    public void Explore_still_cannot_spawn_anything()
    {
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Explore, DysonAgentModes.Drone),
            "Explore cannot spawn subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Explore, DysonAgentModes.Explore),
            "Explore cannot spawn subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Explore, DysonAgentModes.MetaAgentDrone),
            "Explore cannot spawn subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Explore, DysonAgentModes.Plan),
            "Explore cannot spawn subagents.");
    }

    [Fact]
    public void BuiltIns_contains_meta_modes_and_ComposerSelectable_does_not()
    {
        if (!DysonAgentModes.BuiltIns.Contains(DysonAgentModes.MetaAgent, StringComparer.Ordinal))
            throw new InvalidOperationException("BuiltIns must contain Meta Agent.");
        if (!DysonAgentModes.BuiltIns.Contains(DysonAgentModes.MetaAgentDrone, StringComparer.Ordinal))
            throw new InvalidOperationException("BuiltIns must contain Meta Agent Drone.");
        if (DysonAgentModes.ComposerSelectable.Contains(DysonAgentModes.MetaAgent, StringComparer.Ordinal))
            throw new InvalidOperationException("ComposerSelectable must not contain Meta Agent.");
        if (DysonAgentModes.ComposerSelectable.Contains(DysonAgentModes.MetaAgentDrone, StringComparer.Ordinal))
            throw new InvalidOperationException("ComposerSelectable must not contain Meta Agent Drone.");
    }

    private static void AssertOk(VoidResult<string> result)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Expected ok, got: {result.Error}");
    }

    private static void AssertErr(VoidResult<string> result, string expected)
    {
        if (!result.IsError)
            throw new InvalidOperationException($"Expected error '{expected}'.");
        if (!string.Equals(result.Error, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected error '{expected}', got: {result.Error}");
    }
}

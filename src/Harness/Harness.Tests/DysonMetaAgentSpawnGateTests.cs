using System.Text.Json;
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
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgent, DysonAgentModes.BugReview));
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgent, DysonAgentModes.SecurityReview));
    }

    [Fact]
    public void MetaAgent_rejects_classic_drone_and_plan()
    {
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgent, DysonAgentModes.Drone),
            "Meta Agent may only spawn Meta Agent Drone, Explore, Bug Review, or Security Review subagents.");
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
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgentDrone, DysonAgentModes.BugReview));
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgentDrone, DysonAgentModes.SecurityReview));
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgentDrone, DysonAgentModes.Ask),
            "Meta Agent Drone may only spawn Explore, Drone, Bug Review, or Security Review subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.MetaAgentDrone, DysonAgentModes.MetaAgentDrone),
            "No multi-layer Meta Agent Drones; spawn a Drone or Explore.");
    }

    [Fact]
    public void MetaAgent_spawn_tools_keep_useWorktree_and_take_context_files()
    {
        string[] spawnTools =
        [
            "StartAsyncMetaAgentDrone",
            "StartAsyncExploreAgent",
            "StartAsyncBugReviewAgent",
            "StartAsyncSecurityReviewAgent",
        ];

        var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        if (meta.Tools.ContainsKey("CreateAsyncMetaAgentDrone"))
            throw new InvalidOperationException("CreateAsyncMetaAgentDrone must not be registered.");

        foreach (var name in spawnTools)
        {
            if (!meta.Tools.TryGetValue(name, out var tool))
                throw new InvalidOperationException($"Meta Agent catalog missing {name}.");
            AssertSchemaHasContextFilesAndNoAgentMode(tool, name);
        }

        var droneTool = meta.Tools["StartAsyncMetaAgentDrone"];
        if (!droneTool.Description.Contains("existingWorktreePath", StringComparison.Ordinal)
            || !droneTool.Description.Contains("only with useWorktree false", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "StartAsyncMetaAgentDrone description must say existingWorktreePath is only with useWorktree false.");
        }

        using (var schema = JsonDocument.Parse(droneTool.InputSchemaJson))
        {
            var root = schema.RootElement;
            var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
            if (required.Length != 2
                || !required.Contains("task")
                || !required.Contains("useWorktree"))
            {
                throw new InvalidOperationException(
                    "StartAsyncMetaAgentDrone required must be task and useWorktree.");
            }

            var props = root.GetProperty("properties");
            if (!string.Equals(props.GetProperty("useWorktree").GetProperty("type").GetString(), "boolean", StringComparison.Ordinal))
                throw new InvalidOperationException("useWorktree must be boolean.");
            if (!props.TryGetProperty("existingWorktreePath", out _))
                throw new InvalidOperationException("existingWorktreePath must stay on StartAsyncMetaAgentDrone.");
        }

        foreach (var name in new[] { "StartAsyncExploreAgent", "StartAsyncBugReviewAgent", "StartAsyncSecurityReviewAgent" })
            AssertSchemaOmitsWorktree(meta.Tools[name], name);

        var droneCatalog = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.MetaAgentDrone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);
        if (droneCatalog.Tools.ContainsKey("StartAsyncExploreAgent")
            || droneCatalog.Tools.ContainsKey("StartAsyncMetaAgentDrone"))
        {
            throw new InvalidOperationException("Meta Agent Drone must not have explore or drone spawn tools.");
        }

        if (!droneCatalog.Tools.ContainsKey("StartSubagent"))
            throw new InvalidOperationException("Meta Agent Drone must still have StartSubagent.");

        foreach (var name in new[] { "StartAsyncBugReviewAgent", "StartAsyncSecurityReviewAgent" })
        {
            if (!droneCatalog.Tools.TryGetValue(name, out var tool))
                throw new InvalidOperationException($"Meta Agent Drone catalog missing {name}.");
            AssertSchemaHasContextFilesAndNoAgentMode(tool, name);
            AssertSchemaOmitsWorktree(tool, name);
        }

        foreach (var mode in new[] { DysonAgentModes.Drone, DysonAgentModes.Explore, DysonAgentModes.Work })
        {
            var catalog = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), mode);
            foreach (var name in new[] { "StartAsyncBugReviewAgent", "StartAsyncSecurityReviewAgent", "StartAsyncExploreAgent" })
            {
                if (catalog.Tools.ContainsKey(name))
                    throw new InvalidOperationException($"{mode} catalog must not contain {name}.");
            }
        }

        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Drone, DysonAgentModes.BugReview),
            "Drone may only spawn Explore subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Drone, DysonAgentModes.SecurityReview),
            "Drone may only spawn Explore subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Explore, DysonAgentModes.BugReview),
            "Explore cannot spawn subagents.");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Explore, DysonAgentModes.SecurityReview),
            "Explore cannot spawn subagents.");
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

    private static void AssertSchemaHasContextFilesAndNoAgentMode(DysonMcpTool tool, string name)
    {
        if (!tool.InputSchemaJson.Contains("\"contextFiles\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} schema must contain contextFiles.");
        if (tool.InputSchemaJson.Contains("\"agentMode\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} schema must not contain agentMode.");
    }

    private static void AssertSchemaOmitsWorktree(DysonMcpTool tool, string name)
    {
        if (tool.InputSchemaJson.Contains("\"useWorktree\"", StringComparison.Ordinal)
            || tool.InputSchemaJson.Contains("\"existingWorktreePath\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} schema must not contain useWorktree or existingWorktreePath.");
        }
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

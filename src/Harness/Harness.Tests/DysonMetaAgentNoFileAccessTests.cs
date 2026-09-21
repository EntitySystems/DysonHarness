using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// The Meta Agent catalog contains no file/shell/wait/completion tools.
/// This is the load-bearing invariant of Meta Agent mode.
/// </summary>
public class DysonMetaAgentNoFileAccessTests
{
    [Fact]
    public void Meta_agent_catalog_contains_none_of_the_excluded_tools_by_name()
    {
        var pipeline = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);

        foreach (var name in DysonMetaAgentTools.ExcludedToolNames)
        {
            Assert.False(
                pipeline.Tools.ContainsKey(name),
                $"Meta Agent catalog must not contain '{name}'.");
        }

        Assert.True(pipeline.Tools.ContainsKey("LoadSkill"));
        Assert.True(pipeline.Tools.ContainsKey("GetOpenRulesConfig"));

        foreach (var name in new[] { "ListPlans", "SetPlanStatus", "BeginBuildPlan", "DeletePlan" })
        {
            Assert.True(pipeline.Tools.ContainsKey(name), $"Meta Agent catalog must contain '{name}'.");
            AssertNoPathArgument(pipeline.Tools[name]);
        }
    }

    private static void AssertNoPathArgument(DysonMcpTool tool)
    {
        using var doc = JsonDocument.Parse(tool.InputSchemaJson);
        if (!doc.RootElement.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in properties.EnumerateObject())
        {
            Assert.False(
                prop.Name.Contains("path", StringComparison.OrdinalIgnoreCase),
                $"{tool.Name} schema must not accept a path argument (found '{prop.Name}').");
        }
    }
}

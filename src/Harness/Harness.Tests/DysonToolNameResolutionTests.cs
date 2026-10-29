using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Stray model prefixes (submit_, option_, mcp__…__) resolve to the bare catalog name.
/// Exact catalog keys, including names that contain '__', are never rewritten.
/// </summary>
public class DysonToolNameResolutionTests
{
    [Fact]
    public async Task Prefixed_SubmitMetaPlan_stores_the_plan_under_the_bare_name()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        var executor = await fixture.ExecutorAsync(drone, http);

        foreach (var (callId, toolName) in new[]
        {
            ("1", "submit_SubmitMetaPlan"),
            ("2", "mcp__black_scissors__submit_SubmitMetaPlan"),
        })
        {
            var result = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = callId,
                ToolName = toolName,
                Stage = 0,
                ArgumentsJson = $$"""{"title":"Via {{toolName}}","markdown":"# body {{callId}}"}""",
            });
            Assert.False(result.IsError, result.Content);
            Assert.Equal("SubmitMetaPlan", result.ToolName);

            var planId = JsonDocument.Parse(result.Content).RootElement.GetProperty("planId").GetInt64();
            var stored = await fixture.Plans.GetAsync(planId, fixture.WorkDirectoryId);
            Assert.False(stored.IsError, stored.IsError ? stored.Error : "");
            Assert.Equal($"Via {toolName}", stored.Value.Title);
        }

        var listed = await fixture.Plans.ListAsync(fixture.WorkDirectoryId);
        Assert.False(listed.IsError);
        Assert.Equal(2, listed.Value.Count);
    }

    [Fact]
    public async Task Option_prefix_resolves_ReadFile_to_the_bare_name()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "dyson-tool-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        await File.WriteAllTextAsync(Path.Combine(workRoot, "note.txt"), "hello");

        try
        {
            using var http = new HttpClient();
            var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(drone, workRoot, http);

            var result = await executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "1",
                ToolName = "option_ReadFile",
                Stage = 0,
                ArgumentsJson = """{"path":"note.txt"}""",
            });
            Assert.False(result.IsError, result.Content);
            Assert.Equal("ReadFile", result.ToolName);
            Assert.Contains("hello", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Unknown_prefixed_name_keeps_the_catalog_error()
    {
        using var http = new HttpClient();
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(drone, Path.GetTempPath(), http);

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "strike_NoSuchTool",
            Stage = 0,
            ArgumentsJson = "{}",
        });
        Assert.True(result.IsError);
        Assert.Equal("strike_NoSuchTool", result.ToolName);
        Assert.Contains(
            "Tool 'strike_NoSuchTool' is not available in this session's catalog",
            result.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_catalog_name_containing_double_underscore_is_not_rewritten()
    {
        using var http = new HttpClient();
        var drone = new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone);
        drone.McpPipeline.Tools["server__tool"] = new DysonMcpTool
        {
            Name = "server__tool",
            Description = "custom",
            InputSchemaJson = "{}",
        };
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(drone, Path.GetTempPath(), http);

        var result = await executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "1",
            ToolName = "server__tool",
            Stage = 0,
            ArgumentsJson = "{}",
        });
        Assert.False(result.IsError, result.Content);
        Assert.Equal("server__tool", result.ToolName);
        Assert.Contains("server__tool is not implemented yet.", result.Content, StringComparison.Ordinal);
    }
}

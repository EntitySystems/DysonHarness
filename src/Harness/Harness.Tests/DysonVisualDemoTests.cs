using DysonHarness;
using Harness.UI.Demo;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: Demo Mode flag + scripted turn seeds. The session Fact is the check that
/// mock events go through the real tool scheduler (StartSubagent / SubmitSubagentReport).
/// </summary>
public class DysonVisualDemoTests
{
    [Fact]
    public void FromCommandLine_demo_flag_enables()
    {
        var mode = DysonVisualDemoMode.FromCommandLine(["--urls", "http://localhost:5180", "--demo"]);
        Assert.True(mode.Enabled);
    }

    [Fact]
    public void FromCommandLine_visual_demo_flag_enables()
    {
        var mode = DysonVisualDemoMode.FromCommandLine(["--visual-demo"]);
        Assert.True(mode.Enabled);
    }

    [Fact]
    public void FromCommandLine_without_flag_stays_disabled()
    {
        var previous = Environment.GetEnvironmentVariable(DysonVisualDemoMode.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(DysonVisualDemoMode.EnvironmentVariableName, null);
            var mode = DysonVisualDemoMode.FromCommandLine(["--urls", "http://localhost:5180"]);
            Assert.False(mode.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DysonVisualDemoMode.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void SeedTools_root_kickoff_starts_explore_and_files_todos()
    {
        var session = new DemoDysonAgentSession(
            DysonAgentModes.Work,
            new DysonAgentSessionConfig(),
            new DemoDysonAgentProvider(slug: null));
        var turn = DysonSessionInitialization.CreateTurn(DysonVisualDemoScenario.UserPrompt);

        var tools = DysonVisualDemoScenario.SeedTools(session, turn);
        Assert.Contains(tools, t => t.ToolName == "StartSubagent" && t.ArgumentsJson.Contains("Explore"));
        Assert.Contains(tools, t => t.ToolName == "CreateTodo");
        Assert.Contains(tools, t => t.ToolName == "Grep");
        Assert.Contains(tools, t => t.ToolName == "RenameSession");
        Assert.DoesNotContain(tools, t => t.ToolName == "SubmitSubagentReport");
    }

    [Fact]
    public void SeedTools_explore_child_submits_report()
    {
        var child = new DemoDysonAgentSession(
            DysonAgentModes.Explore,
            new DysonAgentSessionConfig(),
            new DemoDysonAgentProvider(slug: null));

        var turn = DysonSessionInitialization.CreateTurn(DysonVisualDemoScenario.ExploreTask);
        var tools = DysonVisualDemoScenario.SeedTools(child, turn);
        Assert.Contains(tools, t => t.ToolName == "SubmitSubagentReport");
        Assert.Contains(tools, t => t.ToolName == "Grep");
        Assert.DoesNotContain(tools, t => t.ToolName == "StartSubagent");
    }

    [Fact]
    public async Task Prompt_visual_demo_spawns_explore_and_child_reports()
    {
        var previous = DysonVisualDemoMode.Current;
        var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-visual-demo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection connection);
        try
        {
            var workdirs = DysonTempDb.WorkDirectories(accessor);
            var sessions = DysonTempDb.Sessions(accessor);
            var models = DysonTempDb.Models(accessor);

            var wd = await workdirs.CreateAsync(workRoot, "VisualDemo");
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            var provider = await models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Mock",
                ProviderKind = DysonProviderKinds.Demo,
            });
            Assert.True(provider.IsSuccess, provider.IsError ? provider.Error : null);

            var slugId = await models.AddSlugAsync(provider.Value, "demo-mock", "Demo Mock");
            Assert.True(slugId.IsSuccess, slugId.IsError ? slugId.Error : null);
            var slug = await models.GetSlugAsync(slugId.Value);
            Assert.True(slug.IsSuccess, slug.IsError ? slug.Error : null);

            var created = await DemoDysonAgentSession.CreateAsync(
                sessions,
                new DemoDysonAgentProvider(slug.Value),
                wd.Value,
                DysonAgentModes.Work,
                workDirectoryAbsolutePath: workRoot);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var session = created.Value;
            DysonVisualDemoMode.Current = new DysonVisualDemoMode(enabled: true)
            {
                AutoPlaySessionId = session.PersistenceId,
            };

            var prompted = await session.PromptAsync(DysonVisualDemoScenario.UserPrompt);
            Assert.True(prompted.IsSuccess, prompted.IsError ? prompted.Error : null);

            var kickoff = Assert.Single(session.Turns);
            Assert.Contains(kickoff.ToolCalls, t => t.ToolName == "StartSubagent");
            Assert.Contains(kickoff.ToolCalls, t => t.ToolName == "CreateTodo");
            Assert.Equal(3, session.Todos.Count);
            Assert.Contains("repository", kickoff.AssistantText, StringComparison.OrdinalIgnoreCase);

            var explore = Assert.Single(session.SubSessions);
            Assert.Equal(DysonAgentModes.Explore, explore.Mode);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (explore.Status != DysonSessionStatus.Completed
                   && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Equal(DysonSessionStatus.Completed, explore.Status);
            Assert.False(string.IsNullOrWhiteSpace(explore.LastReportSummary));
            Assert.Contains(
                explore.Turns.SelectMany(t => t.ToolCalls),
                t => t.ToolName == "SubmitSubagentReport");
        }
        finally
        {
            DysonVisualDemoMode.Current = previous;
            await connection.DisposeAsync();
            try
            {
                Directory.Delete(workRoot, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}

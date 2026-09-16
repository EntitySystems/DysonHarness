using DysonHarness;
using Harness.UI.Demo;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: Demo Mode flag + Remotion promo seeds. The session Fact is the check that
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
    public void UserPrompt_matches_promo_beat()
    {
        Assert.Equal("make a plan to move database calls to repositories", DysonVisualDemoScenario.UserPrompt);
        Assert.Equal(7, DysonVisualDemoScenario.ExpectedSubagentCount);
    }

    [Fact]
    public void SeedTools_root_kickoff_starts_two_explores_and_five_plan_todos()
    {
        var session = new DemoDysonAgentSession(
            DysonAgentModes.Work,
            new DysonAgentSessionConfig(),
            new DemoDysonAgentProvider(slug: null));
        var turn = DysonSessionInitialization.CreateTurn(DysonVisualDemoScenario.UserPrompt);

        var tools = DysonVisualDemoScenario.SeedTools(session, turn);
        Assert.Equal(2, tools.Count(t => t.ToolName == "StartSubagent"));
        Assert.Equal(5, tools.Count(t => t.ToolName == "CreateTodo"));
        Assert.Contains(tools, t => t.ToolName == "StartSubagent" && t.ArgumentsJson.Contains("Explore"));
        Assert.Contains(tools, t => t.ToolName == "Grep");
        Assert.Contains(tools, t => t.ToolName == "ReadFile" && t.ArgumentsJson.Contains("ClientBillService"));
        Assert.Contains(tools, t => t.ToolName == "RenameSession");
        Assert.DoesNotContain(tools, t => t.ToolName == "SubmitSubagentReport");
    }

    [Fact]
    public void SeedTools_inventory_child_submits_report()
    {
        var child = new DemoDysonAgentSession(
            DysonAgentModes.Explore,
            new DysonAgentSessionConfig(),
            new DemoDysonAgentProvider(slug: null));

        var turn = DysonSessionInitialization.CreateTurn(DysonVisualDemoScenario.InventoryClientTask);
        var tools = DysonVisualDemoScenario.SeedTools(child, turn);
        Assert.Contains(tools, t => t.ToolName == "SubmitSubagentReport");
        Assert.Contains(tools, t => t.ToolName == "Grep");
        Assert.Contains(tools, t => t.ToolName == "ReadFile" && t.ArgumentsJson.Contains("ClientBillService"));
        Assert.DoesNotContain(tools, t => t.ToolName == "StartSubagent");
    }

    [Fact]
    public void SeedTools_migrate_child_writes_client_bill_service()
    {
        var child = new DemoDysonAgentSession(
            DysonAgentModes.Drone,
            new DysonAgentSessionConfig(),
            new DemoDysonAgentProvider(slug: null));

        var turn = DysonSessionInitialization.CreateTurn(DysonVisualDemoScenario.MigrateClientTask);
        var tools = DysonVisualDemoScenario.SeedTools(child, turn);
        Assert.Contains(tools, t => t.ToolName == "WriteFile" && t.ArgumentsJson.Contains("ClientBillService"));
        Assert.Contains(tools, t => t.ToolName == "ShellExecute");
        Assert.Contains(tools, t => t.ToolName == "SubmitSubagentReport");
    }

    [Fact]
    public void SeedTools_remaining_migrate_marks_browser_wait_failed()
    {
        var child = new DemoDysonAgentSession(
            DysonAgentModes.Drone,
            new DysonAgentSessionConfig(),
            new DemoDysonAgentProvider(slug: null));

        var turn = DysonSessionInitialization.CreateTurn(DysonVisualDemoScenario.MigrateRestTask);
        var tools = DysonVisualDemoScenario.SeedTools(child, turn);
        var wait = Assert.Single(tools, t => t.ToolName == "BrowserWaitForSelector");
        Assert.True(DysonVisualDemoScenario.IsFailedDemoTool(wait));
        Assert.False(DysonVisualDemoScenario.IsFailedDemoTool(new DysonToolCall
        {
            CallId = "",
            ToolName = "Grep",
            Stage = 0,
        }));
        Assert.Contains(tools, t => t.ToolName == "WriteFile");
        Assert.Contains(tools, t => t.ToolName == "OpenBrowser");
    }

    [Fact]
    public void EnsurePromoWorkspace_writes_client_bill_service()
    {
        var previous = Environment.GetEnvironmentVariable("DYSON_VISUAL_DEMO_WORKDIR");
        var dest = Path.Combine(Path.GetTempPath(), $"dyson-promo-ws-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("DYSON_VISUAL_DEMO_WORKDIR", dest);
            var written = DysonVisualDemoScenario.EnsurePromoWorkspace();
            Assert.Equal(dest, written);
            Assert.True(File.Exists(Path.Combine(dest, "src", "Billing", "ClientBillService.cs")));
            Assert.Contains(
                "BillingDbContext",
                File.ReadAllText(Path.Combine(dest, "src", "Billing", "ClientBillService.cs")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYSON_VISUAL_DEMO_WORKDIR", previous);
            try
            {
                Directory.Delete(dest, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task Prompt_visual_demo_spawns_inventory_explores_then_interfaces_drone()
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
            Assert.Equal(2, kickoff.ToolCalls.Count(t => t.ToolName == "StartSubagent"));
            Assert.Equal(5, kickoff.ToolCalls.Count(t => t.ToolName == "CreateTodo"));
            Assert.Contains(kickoff.ToolCalls, t => t.ToolName == "ReadFile");
            Assert.Equal(5, session.Todos.Count);
            Assert.Contains("ClientBillService", kickoff.AssistantText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("inventory", kickoff.AssistantText, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(2, session.SubSessions.Count);
            Assert.All(session.SubSessions, child => Assert.Equal(DysonAgentModes.Explore, child.Mode));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (session.SubSessions.Any(child => !child.IsTerminal) && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.All(session.SubSessions, child =>
            {
                Assert.Equal(DysonSessionStatus.Completed, child.Status);
                Assert.False(string.IsNullOrWhiteSpace(child.LastReportSummary));
                Assert.Contains(
                    child.Turns.SelectMany(t => t.ToolCalls),
                    t => t.ToolName == "SubmitSubagentReport");
            });

            var handed = await session.PromptSubagentReportProcessingAsync(
                "# Subagent report\n\nBoth inventory reports are in.");
            Assert.True(handed.IsSuccess, handed.IsError ? handed.Error : null);

            var interfaces = Assert.Single(
                session.SubSessions,
                child => (child.DisplayTitle ?? "").Contains("IClientBillRepository", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(DysonAgentModes.Drone, interfaces.Mode);

            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!interfaces.IsTerminal && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.Equal(DysonSessionStatus.Completed, interfaces.Status);
            Assert.Contains(
                interfaces.Turns.SelectMany(t => t.ToolCalls),
                t => t.ToolName == "CreateFile" && t.ArgumentsJson.Contains("IClientBillRepository"));
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

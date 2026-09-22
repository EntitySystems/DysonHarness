using DysonHarness;

namespace Harness.Tests;

public class DysonMetaPlanDisplayPathTests
{
    [Fact]
    public void Format_round_trips_plan_id_and_uses_md_slug()
    {
        var path = DysonMetaPlanDisplayPath.Format(7, "Hello World!");
        Assert.Equal("metaplan:7/hello-world.md", path);
        Assert.True(DysonMetaPlanDisplayPath.TryParse(path, out var id));
        Assert.Equal(7L, id);

        Assert.True(DysonMetaPlanDisplayPath.TryParse("metaplan:7", out id));
        Assert.Equal(7L, id);
        Assert.True(DysonMetaPlanDisplayPath.TryParse("metaplan:7.md", out id));
        Assert.Equal(7L, id);
        Assert.True(DysonMetaPlanDisplayPath.TryParse(@"metaplan:12\other.md", out id));
        Assert.Equal(12L, id);

        Assert.False(DysonMetaPlanDisplayPath.TryParse("skillsdirectory:demo/SKILL.md", out _));
        Assert.False(DysonMetaPlanDisplayPath.TryParse("metaplan:0/plan.md", out _));
        Assert.False(DysonMetaPlanDisplayPath.TryParse("metaplan:", out _));
        Assert.False(DysonMetaPlanDisplayPath.TryParse(null, out _));
    }

    [Fact]
    public void CanBuild_requires_ready_session_and_not_building()
    {
        Assert.True(DysonMetaPlanDisplayPath.CanBuild(DysonPlanStatus.Draft, sessionReady: true));
        Assert.True(DysonMetaPlanDisplayPath.CanBuild(DysonPlanStatus.Completed, sessionReady: true));
        Assert.True(DysonMetaPlanDisplayPath.CanBuild(DysonPlanStatus.Stale, sessionReady: true));
        Assert.False(DysonMetaPlanDisplayPath.CanBuild(DysonPlanStatus.Building, sessionReady: true));
        Assert.False(DysonMetaPlanDisplayPath.CanBuild(DysonPlanStatus.Draft, sessionReady: false));
        Assert.False(DysonMetaPlanDisplayPath.CanBuild(DysonPlanStatus.Building, sessionReady: false));
    }

    [Fact]
    public void FormatBuildPrompt_names_plan_id_and_tool()
    {
        var prompt = DysonMetaPlanDisplayPath.FormatBuildPrompt(7, " Ship it ");
        Assert.Contains("`7`", prompt, StringComparison.Ordinal);
        Assert.Contains("Ship it", prompt, StringComparison.Ordinal);
        Assert.Contains("BeginBuildPlan", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(DysonBeginBuildPlanFlow.BuildInstruction(".dyson/plans/x.md"), prompt);
    }
}

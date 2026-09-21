using DysonHarness;
using Harness.UI.Components.Meta;
using Harness.UI.Demo;

namespace Harness.Tests;

public class MetaChatItemsTests
{
    [Fact]
    public void Build_keeps_user_posts_and_display_info_drops_tools()
    {
        var user = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "Ship the plans column",
        };
        var tools = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.SubagentReportProcessing,
            Instruction = "internal",
            AssistantText = "should not show",
        };
        var post = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.DisplayInfo,
            AssistantText = "Dispatched a drone.",
        };

        var items = MetaChatItems.Build([user, tools, post], []);

        Assert.Equal(2, items.Count);
        Assert.Equal(MetaChatRole.User, items[0].Role);
        Assert.Equal("Ship the plans column", items[0].Text);
        Assert.False(items[0].Pending);
        Assert.Equal(MetaChatRole.Agent, items[1].Role);
        Assert.Equal("Dispatched a drone.", items[1].Text);
    }

    [Fact]
    public void Build_appends_injected_comments_and_queued_pending_bubbles()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "Start mapping",
        };
        var queued = turn.EnqueueUserComment("and also the tests");
        Assert.False(queued.IsError);

        var items = MetaChatItems.Build(
            [turn],
            [new QueuedPrompt(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "queued later",
                "queued later")]);

        Assert.Equal(3, items.Count);
        Assert.Equal("Start mapping", items[0].Text);
        Assert.False(items[0].Pending);
        Assert.Equal("and also the tests", items[1].Text);
        Assert.False(items[1].Pending);
        Assert.Equal("queued later", items[2].Text);
        Assert.True(items[2].Pending);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), items[2].QueuedId);
    }

    [Fact]
    public void Build_queued_bubble_uses_full_Text_not_FirstLine()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var items = MetaChatItems.Build(
            [],
            [new QueuedPrompt(id, "first line", "first line\nsecond line")]);

        var item = Assert.Single(items);
        Assert.Equal("first line\nsecond line", item.Text);
        Assert.True(item.Pending);
        Assert.Equal(id, item.QueuedId);
    }

    [Fact]
    public void SelectExisting_picks_newest_root_meta_agent()
    {
        var older = new DysonSessionSummary
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            AgentMode = DysonAgentModes.MetaAgent,
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastActivityUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new DysonSessionSummary
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            AgentMode = DysonAgentModes.MetaAgent,
            CreatedUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            LastActivityUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
        };
        var work = new DysonSessionSummary
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            AgentMode = DysonAgentModes.Work,
            LastActivityUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var child = new DysonSessionSummary
        {
            Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            ParentSessionId = newer.Id,
            AgentMode = DysonAgentModes.MetaAgent,
            LastActivityUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var picked = MetaAgentSessionLocator.SelectExisting([work, child, older, newer]);
        Assert.NotNull(picked);
        Assert.Equal(newer.Id, picked.Id);

        Assert.Null(MetaAgentSessionLocator.SelectExisting([work]));
    }
}

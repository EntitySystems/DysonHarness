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

    [Fact]
    public void Build_user_bubble_text_is_instruction_not_hidden_paths_or_urls()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "Ship the plans column",
            HiddenInstruction = """
                Attached paths:
                - .dyson/composer-uploads/notes.txt
                """,
        };
        turn.AddUserImage(new DysonBinaryAttachment
        {
            FileName = "shot.jpg",
            Extension = ".jpg",
            MimeType = "image/jpeg",
            Base64Data = "abc",
            RemoteUrl = "https://bucket.example/shot.jpg",
        });

        var item = Assert.Single(MetaChatItems.Build([turn], []));
        Assert.Equal(MetaChatRole.User, item.Role);
        Assert.Equal("Ship the plans column", item.Text);
        Assert.DoesNotContain("HiddenInstruction", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Attached paths", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(".dyson", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("shot.jpg", item.Text, StringComparison.Ordinal);

        var thumb = Assert.Single(item.Thumbs);
        Assert.Equal("https://bucket.example/shot.jpg", thumb.Src);
        Assert.Equal("shot.jpg", thumb.Alt);
        Assert.Equal("notes.txt", Assert.Single(item.FileNames));
    }

    [Fact]
    public void Build_image_only_and_file_only_turns_each_yield_one_user_item()
    {
        var imageOnly = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "   ",
        };
        imageOnly.AddUserImage(new DysonBinaryAttachment
        {
            FileName = "paste.png",
            Extension = ".png",
            MimeType = "image/png",
            Base64Data = "iVBORw0KGgo",
        });

        var fileOnly = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            HiddenInstruction = """
                Attached paths:
                - .dyson/composer-uploads/notes.txt
                """,
        };

        var imageItem = Assert.Single(MetaChatItems.Build([imageOnly], []));
        Assert.Equal(MetaChatRole.User, imageItem.Role);
        Assert.Equal("   ", imageItem.Text);
        Assert.Empty(imageItem.FileNames);
        var thumb = Assert.Single(imageItem.Thumbs);
        Assert.Equal("data:image/png;base64,iVBORw0KGgo", thumb.Src);
        Assert.Equal("paste.png", thumb.Alt);

        var fileItem = Assert.Single(MetaChatItems.Build([fileOnly], []));
        Assert.Equal(MetaChatRole.User, fileItem.Role);
        Assert.Equal("", fileItem.Text);
        Assert.Empty(fileItem.Thumbs);
        Assert.Equal("notes.txt", Assert.Single(fileItem.FileNames));
    }

    [Fact]
    public void Build_attachment_only_queued_row_renders_when_HasAttachments()
    {
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var shown = MetaChatItems.Build(
            [],
            [new QueuedPrompt(id, "", "", HasAttachments: true)]);
        var item = Assert.Single(shown);
        Assert.Equal("", item.Text);
        Assert.True(item.Pending);
        Assert.Equal(id, item.QueuedId);
        Assert.Empty(item.Thumbs);
        Assert.Empty(item.FileNames);
        Assert.DoesNotContain("http", item.Text, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(MetaChatItems.Build(
            [],
            [new QueuedPrompt(id, "", "", HasAttachments: false)]));
    }

    [Fact]
    public void Build_drops_parent_event_keeps_user_instruction_and_display_info()
    {
        var continuation = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
            new DysonAgentInterrupt
            {
                Kind = DysonAgentInterruptKind.SubagentEvent,
                SubagentId = 3,
                EventId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                EventKind = "status",
                Payload = "still working",
            },
            title: "Drone A");
        var parentEvent = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.ParentEvent,
            Instruction = continuation,
        };
        var user = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "Ship the plans column",
        };
        var post = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.DisplayInfo,
            AssistantText = "Dispatched a drone.",
        };

        var items = MetaChatItems.Build([parentEvent, user, post], []);

        Assert.Equal(2, items.Count);
        Assert.Equal(MetaChatRole.User, items[0].Role);
        Assert.Equal("Ship the plans column", items[0].Text);
        Assert.Equal(MetaChatRole.Agent, items[1].Role);
        Assert.Equal("Dispatched a drone.", items[1].Text);
        Assert.DoesNotContain(items, item => item.Text.Contains("eventId:", StringComparison.Ordinal));
        Assert.DoesNotContain(items, item => item.Text.Contains("RespondToSubagentEvent", StringComparison.Ordinal));
    }
}

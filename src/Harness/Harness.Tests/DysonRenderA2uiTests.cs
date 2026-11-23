using System.Text.Json;
using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>RenderA2ui executor: surface on the DisplayInfo turn, inline error + retry, in-place update, ActionButton funcs, QuestionSlot.</summary>
public class DysonRenderA2uiTests
{
    private const string DysonCatalog = "a2ui://dyson/catalogs/v1/catalog.json";

    [Fact]
    public async Task RenderA2ui_valid_message_puts_surface_on_DisplayInfo_turn()
    {
        var (session, executor) = await MetaExecutorAsync();
        var result = await RenderAsync(executor, Args(Create("status", Text("root", "hello")), message: "Status"));
        Assert.False(result.IsError, result.Content);
        using var doc = JsonDocument.Parse(result.Content);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("status", doc.RootElement.GetProperty("surfaceId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("revision").GetInt32());
        Assert.False(doc.RootElement.GetProperty("deleted").GetBoolean());

        var turn = Assert.Single(session.Turns);
        Assert.Equal(DysonAgentTurnKind.DisplayInfo, turn.Kind);
        Assert.Equal("Status", turn.AssistantText);
        Assert.Equal(turn.Id, Guid.Parse(doc.RootElement.GetProperty("turnId").GetString()!));
        Assert.Equal("hello", turn.A2uiSurface!.Components["root"]["text"]!.GetValue<string>());
        Assert.Null(result.HtmlVisualization);
    }

    [Fact]
    public async Task RenderA2ui_malformed_message_is_tool_error_with_inline_LastError_and_session_continues()
    {
        var (session, executor) = await MetaExecutorAsync();
        var bad = Create("status", new JsonObject { ["id"] = "root", ["component"] = "Text", ["text"] = "x", ["html"] = "<b>" });
        DysonToolCallResult? failed = null;
        Assert.Null(await Record.ExceptionAsync(async () => failed = await RenderAsync(executor, Args(bad))));
        Assert.True(failed!.IsError);
        Assert.Contains("\"VALIDATION_FAILED\"", failed.Content, StringComparison.Ordinal);
        Assert.False(failed.EndsCurrentTurn);

        var stub = Assert.Single(session.Turns);
        Assert.NotNull(stub.A2uiSurface!.LastError);
        Assert.Empty(stub.A2uiSurface.Components);

        var retry = await RenderAsync(executor, Args(Create("status", Text("root", "fixed"))));
        Assert.False(retry.IsError, retry.Content);
        var turn = Assert.Single(session.Turns);
        Assert.Equal(stub.Id, turn.Id);
        Assert.Null(turn.A2uiSurface!.LastError);
        Assert.Equal("fixed", turn.A2uiSurface.Components["root"]["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Second_message_same_surfaceId_updates_existing_surface()
    {
        var (session, executor) = await MetaExecutorAsync();
        var first = await RenderAsync(executor, Args(Create("status", Text("root", "one"))));
        Assert.False(first.IsError, first.Content);
        var turnId = Assert.Single(session.Turns).Id;

        var updates = 0;
        session.TurnUpdated += (_, _) => updates++;
        var second = await RenderAsync(executor, Args(
            Env("updateComponents", new JsonObject { ["surfaceId"] = "status", ["components"] = new JsonArray(Text("root", "two")) }),
            Env("updateDataModel", new JsonObject { ["surfaceId"] = "status", ["path"] = "/n", ["value"] = 1 })));
        Assert.False(second.IsError, second.Content);

        var turn = Assert.Single(session.Turns);
        Assert.Equal(turnId, turn.Id);
        Assert.Equal(2, turn.A2uiSurface!.Revision);
        Assert.Equal("two", turn.A2uiSurface.Components["root"]["text"]!.GetValue<string>());
        Assert.Equal(1, updates);
    }

    [Fact]
    public async Task ActionButton_open_plan_resolves_through_builtin_dispatch()
    {
        var (session, executor) = await MetaExecutorAsync();
        var result = await RenderAsync(executor, Args(ActionSurface("links", "open_plan:362")));
        Assert.False(result.IsError, result.Content);

        var surface = Assert.Single(session.Turns).A2uiSurface!;
        var (componentId, func) = Assert.Single(DysonA2uiSurfaces.ActionButtonFuncs(surface));
        Assert.Equal("open", componentId);
        Assert.Equal("open_plan:362", func);

        var resolved = await DysonBuiltInConversationActions.TryResolveAsync(func, Path.GetTempPath());
        Assert.NotNull(resolved);
        Assert.False(resolved!.IsError);
        Assert.Equal(DysonBuiltInConversationKind.OpenPlan, resolved.Value.Kind);
        Assert.Equal(362, resolved.Value.PlanId);
    }

    [Fact]
    public async Task RenderA2ui_Dyson_components_without_catalogId_render_with_no_error_strip()
    {
        var (session, executor) = await MetaExecutorAsync();
        var result = await RenderAsync(executor, Args(
            Env("createSurface", new JsonObject { ["surfaceId"] = "plans" }),
            Env("updateComponents", new JsonObject
            {
                ["surfaceId"] = "plans",
                ["components"] = new JsonArray(
                    new JsonObject { ["id"] = "root", ["component"] = "Row", ["children"] = new JsonArray("open") },
                    new JsonObject { ["id"] = "open", ["component"] = "ActionButton", ["label"] = "Plan 362", ["func"] = "open_plan:362" }),
            })));
        Assert.False(result.IsError, result.Content);

        var surface = Assert.Single(session.Turns).A2uiSurface!;
        Assert.Null(surface.LastError);
        Assert.Equal(1, surface.Revision);
        Assert.Equal(DysonCatalog, surface.Components["open"]["@catalogId"]!.GetValue<string>());

        // A later updateComponents-only call has no createSurface to read a catalogId from.
        var later = await RenderAsync(executor, Args(Env("updateComponents", new JsonObject
        {
            ["surfaceId"] = "plans",
            ["components"] = new JsonArray(
                new JsonObject { ["id"] = "root", ["component"] = "Row", ["children"] = new JsonArray("open", "next") },
                new JsonObject { ["id"] = "next", ["component"] = "ActionButton", ["label"] = "Plan 363", ["func"] = "open_plan:363" }),
        })));
        Assert.False(later.IsError, later.Content);
        surface = Assert.Single(session.Turns).A2uiSurface!;
        Assert.Null(surface.LastError);
        Assert.Equal(2, DysonA2uiSurfaces.ActionButtonFuncs(surface).Count);
    }

    [Theory]
    [InlineData("open_url:javascript:alert(1)", "Only http/https URLs are allowed.")]
    [InlineData("open_file:../x", "Path escapes work directory")]
    public async Task ActionButton_bad_payload_is_tool_error_and_publishes_no_surface(string func, string reason)
    {
        var (session, executor) = await MetaExecutorAsync();
        DysonToolCallResult? result = null;
        Assert.Null(await Record.ExceptionAsync(async () => result = await RenderAsync(executor, Args(ActionSurface("links", func)))));
        Assert.True(result!.IsError);
        Assert.Contains("\"VALIDATION_FAILED\"", result.Content, StringComparison.Ordinal);
        Assert.Contains(reason, result.Content, StringComparison.Ordinal);
        Assert.Contains("/components/1/func", result.Content, StringComparison.Ordinal);

        // Only the inline error stub: nothing rendered.
        var stub = Assert.Single(session.Turns);
        Assert.Empty(stub.A2uiSurface!.Components);
        Assert.Contains(reason, stub.A2uiSurface.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalVideo_escaping_path_fails_but_missing_file_renders()
    {
        var (session, executor) = await MetaExecutorAsync();
        var bad = await RenderAsync(executor, Args(VideoSurface("vid", "../x.mp4")));
        Assert.True(bad.IsError);
        Assert.Contains("Path escapes work directory", bad.Content, StringComparison.Ordinal);
        Assert.Contains("/components/0/path", bad.Content, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(session.Turns).A2uiSurface!.Components);

        // A missing or unsupported file is card text at render time, not a tool error.
        var missing = await RenderAsync(executor, Args(VideoSurface("vid", "no-such-" + Guid.NewGuid().ToString("N") + ".mp4")));
        Assert.False(missing.IsError, missing.Content);
    }

    [Fact]
    public async Task Model3D_escaping_path_fails_at_the_path_pointer_but_missing_file_renders()
    {
        var (session, executor) = await MetaExecutorAsync();
        var bad = await RenderAsync(executor, Args(VideoSurface("m3d", "../x.glb", "Model3D")));
        Assert.True(bad.IsError);
        Assert.Contains("\"VALIDATION_FAILED\"", bad.Content, StringComparison.Ordinal);
        Assert.Contains("/components/0/path", bad.Content, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(session.Turns).A2uiSurface!.Components);

        var missing = await RenderAsync(executor, Args(VideoSurface("m3d", "gone-" + Guid.NewGuid().ToString("N") + ".glb", "Model3D")));
        Assert.False(missing.IsError, missing.Content);
    }

    private static JsonObject VideoSurface(string surfaceId, string path, string component = "LocalVideo")
    {
        var create = Create(surfaceId, new JsonObject { ["id"] = "root", ["component"] = component, ["path"] = path });
        create["createSurface"]!["catalogId"] = DysonCatalog;
        return create;
    }

    [Fact]
    public async Task RenderA2ui_rejects_when_not_meta_agent_and_bad_questionId()
    {
        using var http = new HttpClient();
        var work = new StubSession(DysonAgentModes.Work);
        work.McpPipeline.Tools["RenderA2ui"] = DysonSessionToolsetBuilder
            .Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent)
            .Tools["RenderA2ui"];
        var workExecutor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, Path.GetTempPath(), http);
        var rejected = await RenderAsync(workExecutor, Args(Create("s", Text("root", "x"))));
        Assert.True(rejected.IsError);
        Assert.Equal("RenderA2ui is only available in Meta Agent mode.", rejected.Content);
        Assert.Empty(work.Turns);

        var (session, executor) = await MetaExecutorAsync();
        var badId = await RenderAsync(executor, Args(Create("s", Text("root", "x")), questionId: "nope"));
        Assert.True(badId.IsError);
        Assert.Equal("RenderA2ui: questionId must be a GUID.", badId.Content);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public async Task QuestionSlot_embeds_open_question()
    {
        var (session, executor) = await MetaExecutorAsync();
        var posted = await executor.ExecuteAsync(Call(
            "PostUserQuestion",
            """{"message":"body","question":"Pick","choices":["A","B"]}"""));
        Assert.False(posted.IsError, posted.Content);
        using var doc = JsonDocument.Parse(posted.Content);
        var id = Guid.Parse(doc.RootElement.GetProperty("questionId").GetString()!);

        var layout = Create(
            "review",
            new JsonObject { ["id"] = "root", ["component"] = "Column", ["children"] = new JsonArray("intro", "q") },
            Text("intro", "One decision left:"),
            new JsonObject
            {
                ["id"] = "q",
                ["component"] = "QuestionSlot",
                ["@catalogId"] = DysonCatalog,
                ["questionId"] = id.ToString("D"),
            });
        var rendered = await RenderAsync(executor, Args(layout));
        Assert.False(rendered.IsError, rendered.Content);
        Assert.Equal(2, session.Turns.Count);
        Assert.Equal([id], DysonA2uiSurfaces.QuestionSlotIds(session.Turns[1].A2uiSurface!));

        var unknown = await RenderAsync(executor, Args(Create(
            "other",
            new JsonObject { ["id"] = "root", ["component"] = "Column", ["children"] = new JsonArray("q") },
            new JsonObject
            {
                ["id"] = "q",
                ["component"] = "QuestionSlot",
                ["@catalogId"] = DysonCatalog,
                ["questionId"] = Guid.NewGuid().ToString("D"),
            })));
        Assert.True(unknown.IsError);
        Assert.Contains("\"VALIDATION_FAILED\"", unknown.Content, StringComparison.Ordinal);
        Assert.Contains("/questionId", unknown.Content, StringComparison.Ordinal);
    }

    private static JsonObject ActionSurface(string surfaceId, string func)
    {
        var create = Create(
            surfaceId,
            new JsonObject { ["id"] = "root", ["component"] = "Row", ["children"] = new JsonArray("open") },
            new JsonObject { ["id"] = "open", ["component"] = "ActionButton", ["label"] = "Plan 362", ["func"] = func });
        create["createSurface"]!["catalogId"] = DysonCatalog;
        return create;
    }

    private static string Args(JsonObject envelope, string? questionId = null, string? message = null) =>
        ArgsOf(new[] { envelope }, questionId, message);

    private static string Args(JsonObject first, JsonObject second) => ArgsOf(new[] { first, second }, null, null);

    private static string ArgsOf(JsonObject[] envelopes, string? questionId, string? message)
    {
        var args = new JsonObject { ["messages"] = new JsonArray(envelopes) };
        if (questionId is not null)
            args["questionId"] = questionId;
        if (message is not null)
            args["message"] = message;
        return args.ToJsonString();
    }

    private static JsonObject Env(string kind, JsonObject body) =>
        new() { ["version"] = "1.0", [kind] = body };

    private static JsonObject Create(string surfaceId, params JsonObject[] components) =>
        Env("createSurface", new JsonObject { ["surfaceId"] = surfaceId, ["components"] = new JsonArray(components) });

    private static JsonObject Text(string id, string text) =>
        new() { ["id"] = id, ["component"] = "Text", ["text"] = text };

    private static Task<DysonToolCallResult> RenderAsync(DysonWorkspaceToolExecutor executor, string args) =>
        executor.ExecuteAsync(Call("RenderA2ui", args));

    private static DysonToolCall Call(string toolName, string args) => new()
    {
        CallId = Guid.NewGuid().ToString("N"),
        ToolName = toolName,
        Stage = 1,
        ArgumentsJson = args,
    };

    private static async Task<(StubSession Session, DysonWorkspaceToolExecutor Executor)> MetaExecutorAsync()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), new HttpClient());
        return (session, executor);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

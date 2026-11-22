using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Session-level A2UI: TryApplyA2ui, NoteA2uiError, QuestionSlot checks, and the return handler.</summary>
public class DysonA2uiSessionTests
{
    private const string DysonCatalog = "a2ui://dyson/catalogs/v1/catalog.json";

    [Fact]
    public void Same_surfaceId_updates_existing_surface_in_place()
    {
        var session = new StubSession();
        var first = session.TryApplyA2ui(Parse(Create("s1", Text("root", "one"))), null, "Status");
        Assert.False(first.IsError, first.IsError ? first.Error.Message : null);

        var updates = 0;
        session.TurnUpdated += (_, _) => updates++;
        var second = session.TryApplyA2ui(
            Parse(
                Env("updateComponents", new JsonObject { ["surfaceId"] = "s1", ["components"] = new JsonArray(Text("root", "two")) }),
                Env("updateDataModel", new JsonObject { ["surfaceId"] = "s1", ["path"] = "/x", ["value"] = 1 })),
            null,
            null);
        Assert.False(second.IsError, second.IsError ? second.Error.Message : null);

        var turn = Assert.Single(session.Turns);
        Assert.Equal(DysonAgentTurnKind.DisplayInfo, turn.Kind);
        Assert.Equal("Status", turn.AssistantText);
        Assert.Equal(first.Value.TurnId, second.Value.TurnId);
        Assert.Equal(turn.Id, second.Value.TurnId);
        Assert.Equal(2, turn.A2uiSurface!.Revision);
        Assert.Equal("two", turn.A2uiSurface.Components["root"]["text"]!.GetValue<string>());
        Assert.Equal(1, updates);
    }

    [Fact]
    public void UpdateUserQuestion_drops_forwarded_surface()
    {
        var session = new StubSession();
        var q = session.AppendDisplayInfoTurn("body", userQuestion: Question()).UserQuestion!;
        Assert.False(session.TryApplyA2ui(Parse(Create("form", Text("root", "pick"))), q.Id, null).IsError);

        var replaced = session.TryReplaceUserQuestion(q.Id, q with { Choices = ["C", "D"] }, "new body", null);

        Assert.True(replaced.IsSuccess);
        Assert.Null(session.Turns[0].A2uiSurface);
    }

    [Fact]
    public void CreateSurface_twice_is_INTEGRITY_ERROR_and_keeps_first_surface()
    {
        var session = new StubSession();
        Assert.False(session.TryApplyA2ui(Parse(Create("s1", Text("root", "one"))), null, null).IsError);
        var again = session.TryApplyA2ui(Parse(Create("s1", Text("root", "two"))), null, null);
        Assert.True(again.IsError);
        Assert.Equal(DysonA2uiProtocol.IntegrityError, again.Error.Code);
        var turn = Assert.Single(session.Turns);
        Assert.Equal("one", turn.A2uiSurface!.Components["root"]["text"]!.GetValue<string>());
        Assert.Equal(1, turn.A2uiSurface.Revision);
    }

    [Fact]
    public void DeleteSurface_clears_surface_and_question_card_remains()
    {
        var session = new StubSession();
        var q = session.AppendDisplayInfoTurn("body", userQuestion: Question()).UserQuestion!;
        var bound = session.TryApplyA2ui(Parse(Create("form", Text("root", "pick"))), q.Id, null);
        Assert.False(bound.IsError, bound.IsError ? bound.Error.Message : null);
        Assert.Equal(q.Id, session.Turns[0].A2uiSurface!.QuestionId);

        var deleted = session.TryApplyA2ui(
            Parse(Env("deleteSurface", new JsonObject { ["surfaceId"] = "form" })), null, null);
        Assert.False(deleted.IsError, deleted.IsError ? deleted.Error.Message : null);
        Assert.True(deleted.Value.Deleted);
        Assert.Single(session.Turns);
        Assert.Null(session.Turns[0].A2uiSurface);
        Assert.Equal(q, session.Turns[0].UserQuestion);
        Assert.Equal("body", session.Turns[0].AssistantText);
    }

    [Fact]
    public void NoteA2uiError_stub_then_retry_lands_on_same_turn()
    {
        var session = new StubSession();
        var error = new DysonA2uiError(DysonA2uiProtocol.ValidationFailed, "s1", "/messages/0", "bad");
        session.NoteA2uiError("s1", null, "View", error);
        var stub = Assert.Single(session.Turns);
        Assert.Equal("bad", stub.A2uiSurface!.LastError);
        Assert.Empty(stub.A2uiSurface.Components);

        session.NoteA2uiError("s1", null, "View", error);
        Assert.Single(session.Turns);

        var ok = session.TryApplyA2ui(Parse(Create("s1", Text("root", "fixed"))), null, null);
        Assert.False(ok.IsError, ok.IsError ? ok.Error.Message : null);
        Assert.Equal(stub.Id, Assert.Single(session.Turns).Id);
        Assert.Null(stub.A2uiSurface!.LastError);
        Assert.Contains("root", stub.A2uiSurface.Components.Keys);

        session.NoteA2uiError("s1", null, null, error with { Message = "later" });
        Assert.Equal("later", stub.A2uiSurface!.LastError);
        Assert.Equal("fixed", stub.A2uiSurface.Components["root"]["text"]!.GetValue<string>());
    }

    [Fact]
    public void QuestionSlot_session_checks_reject_bad_ids()
    {
        var session = new StubSession();
        var open = session.AppendDisplayInfoTurn("q", userQuestion: Question()).UserQuestion!.Id;
        var answered = session.AppendDisplayInfoTurn("q2", userQuestion: Question()).UserQuestion!.Id;
        Assert.False(session.TryRecordUserQuestionAnswer(answered, ["A"], null).IsError);
        var secure = Guid.NewGuid();
        session.AppendDisplayInfoTurn(
            "secrets",
            secureDataRequest: new DysonSecureDataRequest(secure, [new DysonSecureDataField("k", "d")], Outcome: null));
        var turns = session.Turns.Count;

        foreach (var bad in new[] { Guid.NewGuid(), answered, secure })
            AssertSlotRejected(session, SlotSurface("lay", bad), "not found or not open", turns);

        AssertSlotRejected(session, SlotSurface("lay", open, open), "already embedded in this surface", turns);

        Assert.False(session.TryApplyA2ui(Parse(SlotSurface("first", open)), null, null).IsError);
        turns = session.Turns.Count;
        AssertSlotRejected(session, SlotSurface("second", open), "already embedded in surface first", turns);

        var own = session.AppendDisplayInfoTurn("own", userQuestion: Question()).UserQuestion!.Id;
        turns = session.Turns.Count;
        var r = session.TryApplyA2ui(Parse(SlotSurface("bound", own)), own, null);
        Assert.True(r.IsError);
        Assert.Contains("own bound question", r.Error.Message, StringComparison.Ordinal);
        Assert.EndsWith("/questionId", r.Error.Path, StringComparison.Ordinal);
        Assert.Equal(turns, session.Turns.Count);
        Assert.Null(session.Turns[^1].A2uiSurface);
    }

    [Fact]
    public void QuestionSlot_embeds_open_question()
    {
        var session = new StubSession();
        var id = session.AppendDisplayInfoTurn("q", userQuestion: Question()).UserQuestion!.Id;
        var r = session.TryApplyA2ui(Parse(SlotSurface("lay", id)), null, null);
        Assert.False(r.IsError, r.IsError ? r.Error.Message : null);
        Assert.Equal([id], DysonA2uiSurfaces.QuestionSlotIds(session.Turns[1].A2uiSurface!));
    }

    [Fact]
    public void Withdraw_clears_bound_surface()
    {
        var session = new StubSession();
        var id = session.AppendDisplayInfoTurn("q", userQuestion: Question()).UserQuestion!.Id;
        Assert.False(session.TryApplyA2ui(Parse(Create("form", Text("root", "x"))), id, null).IsError);
        Assert.False(session.TryWithdrawUserQuestion(id).IsError);
        Assert.Null(session.Turns[0].A2uiSurface);
    }

    [Fact]
    public void Forwarded_question_resolves_to_normal_answer()
    {
        var session = new StubSession();
        var question = new DysonUserQuestion(Guid.NewGuid(), "Pick", ["A", "B"], false, true, null);
        session.AppendDisplayInfoTurn("body", userQuestion: question);
        var applied = session.TryApplyA2ui(Parse(FormSurface("deploy_form")), question.Id, null);
        Assert.False(applied.IsError, applied.IsError ? applied.Error.Message : null);

        // Single-select with custom allowed: choice and custom together is invalid, so send the custom alone first.
        var both = DysonA2uiReturnHandler.Prepare(
            session, applied.Value.TurnId, "deploy_form", "submit",
            new JsonObject { ["sel"] = new JsonArray("B"), ["note"] = "ship it" });
        var multi = new StubSession();
        var mq = new DysonUserQuestion(Guid.NewGuid(), "Pick", ["A", "B"], true, true, null);
        multi.AppendDisplayInfoTurn("body", userQuestion: mq);
        var mApplied = multi.TryApplyA2ui(Parse(FormSurface("deploy_form")), mq.Id, null);
        Assert.False(mApplied.IsError, mApplied.IsError ? mApplied.Error.Message : null);
        var prepared = DysonA2uiReturnHandler.Prepare(
            multi, mApplied.Value.TurnId, "deploy_form", "submit",
            new JsonObject { ["sel"] = new JsonArray("B"), ["note"] = "ship it" });
        Assert.False(prepared.IsError, prepared.IsError ? prepared.Error : null);
        Assert.Equal(mq.Id, prepared.Value.QuestionId);
        Assert.Equal(["B"], prepared.Value.Selected);
        Assert.Equal("ship it", prepared.Value.CustomText);

        var recorded = multi.TryRecordUserQuestionAnswer(mq.Id, prepared.Value.Selected, prepared.Value.CustomText);
        Assert.False(recorded.IsError, recorded.IsError ? recorded.Error : null);
        var answeredQ = multi.FindUserQuestion(mq.Id)!;
        Assert.NotNull(answeredQ.Answer);
        Assert.Equal(
            $"Answer to question #{mq.Id:D} \"Pick\": B; Other: ship it",
            DysonUserQuestion.FormatAnswerPrompt(answeredQ, null));
        Assert.Contains("questionId:", DysonUserQuestion.FormatHiddenInstruction(answeredQ), StringComparison.Ordinal);

        var again = DysonA2uiReturnHandler.Prepare(
            multi, mApplied.Value.TurnId, "deploy_form", "submit",
            new JsonObject { ["sel"] = new JsonArray("B"), ["note"] = "" });
        Assert.True(again.IsError);
        Assert.Contains("already answered", again.Error, StringComparison.Ordinal);

        // Single-select: choice plus custom fails ValidateAnswer and records nothing.
        Assert.True(both.IsError);
        Assert.Null(session.FindUserQuestion(question.Id)!.Answer);
    }

    [Fact]
    public void Return_handler_negatives_record_nothing()
    {
        var session = new StubSession();
        var question = new DysonUserQuestion(Guid.NewGuid(), "Pick", ["A", "B"], true, true, null);
        session.AppendDisplayInfoTurn("body", userQuestion: question);
        var applied = session.TryApplyA2ui(Parse(FormSurface("f")), question.Id, null);
        Assert.False(applied.IsError, applied.IsError ? applied.Error.Message : null);
        var turnId = applied.Value.TurnId;

        var unknown = DysonA2uiReturnHandler.Prepare(
            session, turnId, "f", "submit", new JsonObject { ["sel"] = new JsonArray("Z"), ["note"] = "" });
        Assert.True(unknown.IsError);

        var failingCheck = DysonA2uiReturnHandler.Prepare(
            session, turnId, "f", "submit", new JsonObject { ["sel"] = new JsonArray(), ["note"] = "" });
        Assert.True(failingCheck.IsError);

        var wrongSurface = DysonA2uiReturnHandler.Prepare(
            session, turnId, "other", "submit", new JsonObject { ["sel"] = new JsonArray("A") });
        Assert.True(wrongSurface.IsError);

        Assert.Null(session.FindUserQuestion(question.Id)!.Answer);
    }

    [Fact]
    public void Derived_card_action_resolves_to_normal_answer()
    {
        var session = new StubSession();
        var question = new DysonUserQuestion(Guid.NewGuid(), "Pick", ["A", "B"], false, true, null);
        session.AppendDisplayInfoTurn("body", userQuestion: question);
        var surfaceId = "dyson_question_" + question.Id.ToString("N");

        var prepared = DysonA2uiReturnHandler.Prepare(
            session, Guid.Empty, surfaceId, "submit",
            new JsonObject { ["selected"] = new JsonArray("B"), ["custom"] = "" });
        Assert.False(prepared.IsError, prepared.IsError ? prepared.Error : null);
        Assert.Equal(["B"], prepared.Value.Selected);

        Assert.False(session.TryRecordUserQuestionAnswer(
            prepared.Value.QuestionId, prepared.Value.Selected, prepared.Value.CustomText).IsError);
        Assert.Equal(
            $"Answer to question #{question.Id:D} \"Pick\": B",
            DysonUserQuestion.FormatAnswerPrompt(session.FindUserQuestion(question.Id)!, null));

        var again = DysonA2uiReturnHandler.Prepare(
            session, Guid.Empty, surfaceId, "submit",
            new JsonObject { ["selected"] = new JsonArray("A"), ["custom"] = "" });
        Assert.True(again.IsError);
        Assert.Contains("already answered", again.Error, StringComparison.Ordinal);

        var forged = DysonA2uiReturnHandler.Prepare(
            session, Guid.Empty, "dyson_question_" + Guid.NewGuid().ToString("N"), "submit",
            new JsonObject { ["selected"] = new JsonArray("A") });
        Assert.True(forged.IsError);
    }

    private static void AssertSlotRejected(StubSession session, JsonObject envelope, string reason, int turns)
    {
        DysonA2uiError? error = null;
        Assert.Null(Record.Exception(() =>
        {
            var r = session.TryApplyA2ui(Parse(envelope), null, null);
            Assert.True(r.IsError);
            error = r.Error;
        }));
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, error!.Code);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.EndsWith("/questionId", error.Path, StringComparison.Ordinal);
        Assert.Equal(turns, session.Turns.Count);
    }

    private static DysonUserQuestion Question() => new(Guid.NewGuid(), "Pick", ["A", "B"], false, true, null);

    private static IReadOnlyList<DysonA2uiMessage> Parse(params JsonObject[] envelopes)
    {
        var parsed = DysonA2uiValidator.Parse(new JsonArray(envelopes).ToJsonString());
        Assert.False(parsed.IsError, parsed.IsError ? parsed.Error.ToWireJson() : null);
        return parsed.Value;
    }

    private static JsonObject Env(string kind, JsonObject body) =>
        new() { ["version"] = "1.0", [kind] = body };

    private static JsonObject Create(string surfaceId, params JsonObject[] components) =>
        Env("createSurface", new JsonObject { ["surfaceId"] = surfaceId, ["components"] = new JsonArray(components) });

    private static JsonObject Text(string id, string text) =>
        new() { ["id"] = id, ["component"] = "Text", ["text"] = text };

    private static JsonObject SlotSurface(string surfaceId, params Guid[] questionIds)
    {
        var children = new JsonArray();
        var components = new List<JsonObject>();
        for (var i = 0; i < questionIds.Length; i++)
        {
            children.Add("q" + i);
            components.Add(new JsonObject
            {
                ["id"] = "q" + i,
                ["component"] = "QuestionSlot",
                ["@catalogId"] = DysonCatalog,
                ["questionId"] = questionIds[i].ToString("D"),
            });
        }

        components.Insert(0, new JsonObject { ["id"] = "root", ["component"] = "Column", ["children"] = children });
        return Create(surfaceId, [.. components]);
    }

    private static JsonObject FormSurface(string surfaceId)
    {
        var create = Create(
            surfaceId,
            new JsonObject { ["id"] = "root", ["component"] = "Column", ["children"] = new JsonArray("target", "note", "submit") },
            new JsonObject
            {
                ["id"] = "target",
                ["component"] = "ChoicePicker",
                ["variant"] = "multipleSelection",
                ["options"] = new JsonArray(
                    new JsonObject { ["label"] = "A", ["value"] = "A" },
                    new JsonObject { ["label"] = "B", ["value"] = "B" }),
                ["value"] = new JsonObject { ["@path"] = "/sel" },
            },
            new JsonObject
            {
                ["id"] = "note",
                ["component"] = "TextField",
                ["label"] = "Notes",
                ["value"] = new JsonObject { ["@path"] = "/note" },
            },
            new JsonObject
            {
                ["id"] = "submit",
                ["component"] = "Button",
                ["child"] = "submit_label",
                ["action"] = new JsonObject
                {
                    ["event"] = new JsonObject
                    {
                        ["name"] = "answer",
                        ["context"] = new JsonObject
                        {
                            ["selected"] = new JsonObject { ["@path"] = "/sel" },
                            ["custom"] = new JsonObject { ["@path"] = "/note" },
                        },
                    },
                },
                ["@checks"] = new JsonArray(new JsonObject
                {
                    ["condition"] = new JsonObject
                    {
                        ["@call"] = "required",
                        ["args"] = new JsonObject { ["value"] = new JsonObject { ["@path"] = "/sel" } },
                    },
                    ["message"] = "Pick one.",
                }),
            },
            Text("submit_label", "Submit"));
        create["createSurface"]!["dataModel"] = new JsonObject { ["sel"] = new JsonArray(), ["note"] = "" };
        return create;
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.MetaAgent,
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

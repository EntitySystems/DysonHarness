using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>PostUserQuestion is root Meta Agent only. The card locks through one JSON blob.</summary>
public class DysonPostUserQuestionTests
{
    [Fact]
    public void PostUserQuestion_schema_is_on_root_meta_catalog_only()
    {
        var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        Assert.True(meta.Tools.TryGetValue("PostUserQuestion", out var tool));
        Assert.Contains(
            "When asking the user a question, do not use PostConversationMessage; use PostUserQuestion instead, with a custom message.",
            tool!.Description,
            StringComparison.Ordinal);
        Assert.Contains("does not end the turn", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Do not use this to ask the user a question. A question goes through PostUserQuestion, with a custom message.",
            meta.Tools["PostConversationMessage"].Description,
            StringComparison.Ordinal);

        foreach (var name in new[] { "ListUserQuestions", "UpdateUserQuestion", "DeleteUserQuestion" })
            Assert.True(meta.Tools.ContainsKey(name), name);

        foreach (var name in new[] { "UpdateUserQuestion", "DeleteUserQuestion" })
            Assert.Contains("call ListUserQuestions", meta.Tools[name].Description, StringComparison.Ordinal);

        Assert.Contains("call ListUserQuestions", tool.Description, StringComparison.Ordinal);
        Assert.Contains(
            "whenever you are not sure which questions are still open",
            meta.Tools["ListUserQuestions"].Description,
            StringComparison.Ordinal);
        Assert.Contains("outdated", meta.Tools["UpdateUserQuestion"].Description, StringComparison.Ordinal);
        Assert.Contains("outdated", meta.Tools["DeleteUserQuestion"].Description, StringComparison.Ordinal);

        var work = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.Work);
        Assert.False(work.Tools.ContainsKey("PostUserQuestion"));

        var explore = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.Explore);
        var drone = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.Drone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);
        var metaDrone = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.MetaAgentDrone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);
        var sidecar = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.VoiceSidecar);
        foreach (var pipeline in new[] { work, explore, drone, metaDrone, sidecar })
        {
            Assert.False(pipeline.Tools.ContainsKey("ListUserQuestions"));
            Assert.False(pipeline.Tools.ContainsKey("UpdateUserQuestion"));
            Assert.False(pipeline.Tools.ContainsKey("DeleteUserQuestion"));
        }
    }

    [Fact]
    public async Task PostUserQuestion_rejects_when_not_meta_agent()
    {
        using var http = new HttpClient();
        var work = new StubSession(DysonAgentModes.Work);
        work.McpPipeline.Tools["PostUserQuestion"] = DysonSessionToolsetBuilder
            .Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent)
            .Tools["PostUserQuestion"];
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, Path.GetTempPath(), http);
        var rejected = await ExecuteAsync(executor, ValidArgs("Pick one"));
        Assert.True(rejected.IsError);
        Assert.Equal("PostUserQuestion is only available in Meta Agent mode.", rejected.Content);
        Assert.Empty(work.Turns);
    }

    [Fact]
    public async Task PostUserQuestion_rejects_blank_question_and_fewer_than_two_choices()
    {
        var (session, executor) = await MetaExecutorAsync();
        var blank = await ExecuteAsync(executor, """{"message":"body","question":"  ","choices":["A","B"]}""");
        Assert.True(blank.IsError);
        Assert.Equal("PostUserQuestion: question is required.", blank.Content);

        var one = await ExecuteAsync(executor, """{"message":"body","question":"Pick","choices":["A"]}""");
        Assert.True(one.IsError);
        Assert.Equal("PostUserQuestion: choices require at least 2.", one.Content);

        var missing = await ExecuteAsync(executor, """{"message":"body","question":"Pick"}""");
        Assert.True(missing.IsError);
        Assert.Equal("PostUserQuestion: choices are required.", missing.Content);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public async Task PostUserQuestion_rejects_more_than_12_choices_and_duplicates()
    {
        var (session, executor) = await MetaExecutorAsync();
        var many = string.Join(",", Enumerable.Range(0, 13).Select(i => $"\"c{i}\""));
        var over = await ExecuteAsync(
            executor,
            "{\"message\":\"body\",\"question\":\"Pick\",\"choices\":[" + many + "]}");
        Assert.True(over.IsError);
        Assert.Equal("PostUserQuestion: choices cannot exceed 12.", over.Content);

        var dup = await ExecuteAsync(executor, """{"message":"body","question":"Pick","choices":["A","A"]}""");
        Assert.True(dup.IsError);
        Assert.Equal("PostUserQuestion: choices must be unique.", dup.Content);

        var trimmed = await ExecuteAsync(executor, """{"message":"body","question":"Pick","choices":["A"," A "]}""");
        Assert.True(trimmed.IsError);
        Assert.Equal("PostUserQuestion: choices must be unique.", trimmed.Content);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public async Task PostUserQuestion_defaults_multiSelect_false_and_allowCustomAnswer_true()
    {
        var (session, executor) = await MetaExecutorAsync();
        var posted = await ExecuteAsync(executor, ValidArgs("Which one?"));
        Assert.False(posted.IsError, posted.Content);
        Assert.False(posted.EndsCurrentTurn);
        using var doc = JsonDocument.Parse(posted.Content);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var id = Guid.Parse(doc.RootElement.GetProperty("questionId").GetString()!);
        var bubble = Assert.Single(session.Turns);
        Assert.Equal(DysonAgentTurnKind.DisplayInfo, bubble.Kind);
        Assert.Equal("Which one?", bubble.AssistantText);
        var question = bubble.UserQuestion;
        Assert.NotNull(question);
        Assert.Equal(id, question!.Id);
        Assert.Equal("Pick", question.Question);
        Assert.False(question.MultiSelect);
        Assert.True(question.AllowCustomAnswer);
        Assert.Null(question.Answer);
        Assert.Equal(["A", "B"], question.Choices);
    }

    [Fact]
    public async Task PostUserQuestion_rejects_bad_actions_over_8()
    {
        var (session, executor) = await MetaExecutorAsync();
        var actions = string.Join(
            ",",
            Enumerable.Range(0, 9).Select(i => $"{{\"name\":\"n{i}\",\"func\":\"f{i}\"}}"));
        var over = await ExecuteAsync(
            executor,
            ValidArgs("Which one?")[..^1] + ",\"actions\":[" + actions + "]}");
        Assert.True(over.IsError);
        Assert.Equal("PostUserQuestion: actions cannot exceed 8.", over.Content);

        var bad = await ExecuteAsync(executor, "{");
        Assert.True(bad.IsError);
        Assert.Equal("PostUserQuestion: invalid JSON arguments.", bad.Content);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public void Answer_single_rejects_two_choices_and_choice_plus_custom()
    {
        var question = Sample(multi: false, allowCustom: true);
        var two = DysonUserQuestion.ValidateAnswer(question, ["A", "B"], null, DateTime.UtcNow);
        Assert.True(two.IsError);
        Assert.Equal("PostUserQuestion: choose one option or a custom answer.", two.Error);

        var both = DysonUserQuestion.ValidateAnswer(question, ["A"], "other", DateTime.UtcNow);
        Assert.True(both.IsError);
        Assert.Equal("PostUserQuestion: choose one option or a custom answer, not both.", both.Error);
    }

    [Fact]
    public void Answer_multi_accepts_several_choices_and_custom()
    {
        var question = Sample(multi: true, allowCustom: true);
        var both = DysonUserQuestion.ValidateAnswer(question, ["B", "A"], " extra ", DateTime.UtcNow);
        Assert.False(both.IsError, both.Error);
        Assert.Equal(["A", "B"], both.Value.SelectedChoices);
        Assert.Equal("extra", both.Value.CustomText);
        Assert.Equal(DateTimeKind.Utc, both.Value.AnsweredUtc.Kind);
    }

    [Fact]
    public void Answer_requires_a_choice_or_nonempty_custom()
    {
        var single = Sample(multi: false, allowCustom: true);
        var none = DysonUserQuestion.ValidateAnswer(single, [], "  ", DateTime.UtcNow);
        Assert.True(none.IsError);
        Assert.Equal("PostUserQuestion: select at least one choice or a custom answer.", none.Error);

        var custom = DysonUserQuestion.ValidateAnswer(single, null, "typed", DateTime.UtcNow);
        Assert.False(custom.IsError, custom.Error);
        Assert.Equal("Answer: typed", DysonUserQuestion.FormatVisibleInstruction(single with { Answer = custom.Value }));

        var multi = Sample(multi: true, allowCustom: true);
        var empty = DysonUserQuestion.ValidateAnswer(multi, [], null, DateTime.UtcNow);
        Assert.True(empty.IsError);
        Assert.Equal("PostUserQuestion: select at least one choice or a custom answer.", empty.Error);
    }

    [Fact]
    public void Answer_rejects_unknown_choice_and_custom_when_disabled()
    {
        var question = Sample(multi: true, allowCustom: false);
        var unknown = DysonUserQuestion.ValidateAnswer(question, ["C"], null, DateTime.UtcNow);
        Assert.True(unknown.IsError);
        Assert.Equal("PostUserQuestion: unknown choice.", unknown.Error);

        var custom = DysonUserQuestion.ValidateAnswer(question, ["A"], "nope", DateTime.UtcNow);
        Assert.True(custom.IsError);
        Assert.Equal("PostUserQuestion: custom answer is not allowed.", custom.Error);

        var dup = DysonUserQuestion.ValidateAnswer(question, ["A", "A"], null, DateTime.UtcNow);
        Assert.True(dup.IsError);
        Assert.Equal("PostUserQuestion: duplicate choice.", dup.Error);
    }

    [Fact]
    public void Answer_rejects_second_submit()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var posted = session.AppendDisplayInfoTurn("body", userQuestion: Sample(multi: false, allowCustom: true));
        var id = posted.UserQuestion!.Id;
        var first = session.TryRecordUserQuestionAnswer(id, ["A"], null);
        Assert.False(first.IsError, first.Error);

        var second = session.TryRecordUserQuestionAnswer(id, ["B"], null);
        Assert.True(second.IsError);
        Assert.Equal("PostUserQuestion: already answered.", second.Error);
        Assert.Equal(["A"], posted.UserQuestion!.Answer!.SelectedChoices);
    }

    [Fact]
    public void Answer_old_question_after_newer_display_info_still_records()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var older = session.AppendDisplayInfoTurn("first", userQuestion: Sample(multi: false, allowCustom: false));
        session.AppendDisplayInfoTurn("newer bubble");
        var recorded = session.TryRecordUserQuestionAnswer(older.UserQuestion!.Id, ["B"], null);
        Assert.False(recorded.IsError, recorded.Error);
        Assert.Equal(["B"], older.UserQuestion!.Answer!.SelectedChoices);
        Assert.Null(session.Turns[^1].UserQuestion);
    }

    [Fact]
    public void UserQuestion_round_trips_through_turn_entity_and_reloads_locked()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var posted = session.AppendDisplayInfoTurn(
            "body",
            userQuestion: Sample(multi: true, allowCustom: true));
        var id = posted.UserQuestion!.Id;
        var recorded = session.TryRecordUserQuestionAnswer(id, ["A", "B"], "note");
        Assert.False(recorded.IsError, recorded.Error);

        var entity = DysonTurnPersistence.ToEntity(posted, Guid.NewGuid(), sequence: 1);
        var reloaded = DysonUserQuestionSerializer.Deserialize(entity.UserQuestionJson);
        Assert.NotNull(reloaded);
        Assert.Equal(id, reloaded!.Id);
        Assert.Equal(["A", "B"], reloaded.Choices);
        Assert.NotNull(reloaded.Answer);
        Assert.Equal(["A", "B"], reloaded.Answer!.SelectedChoices);
        Assert.Equal("note", reloaded.Answer.CustomText);
        Assert.Equal(DateTimeKind.Utc, reloaded.Answer.AnsweredUtc.Kind);
        Assert.Equal(recorded.Value.AnsweredUtc, reloaded.Answer.AnsweredUtc);
        Assert.DoesNotContain("questionId", DysonUserQuestion.FormatVisibleInstruction(reloaded), StringComparison.Ordinal);
        Assert.Contains(id.ToString("D"), DysonUserQuestion.FormatHiddenInstruction(reloaded), StringComparison.Ordinal);

        Assert.Null(DysonUserQuestionSerializer.Deserialize(" "));
        Assert.Null(DysonUserQuestionSerializer.Deserialize("{"));

        var stale = session.TryClearUserQuestionAnswer(id, recorded.Value.AnsweredUtc.AddMinutes(-1));
        Assert.True(stale.IsError);
        Assert.Equal(["A", "B"], posted.UserQuestion!.Answer!.SelectedChoices);

        var cleared = session.TryClearUserQuestionAnswer(id, recorded.Value.AnsweredUtc);
        Assert.False(cleared.IsError, cleared.Error);
        var newer = session.TryRecordUserQuestionAnswer(id, ["B"], null);
        Assert.False(newer.IsError, newer.Error);
        var wiped = session.TryClearUserQuestionAnswer(id, recorded.Value.AnsweredUtc);
        Assert.True(wiped.IsError);
        Assert.Equal(["B"], posted.UserQuestion!.Answer!.SelectedChoices);
        Assert.NotEqual(recorded.Value.AnsweredUtc, posted.UserQuestion.Answer.AnsweredUtc);
    }

    [Fact]
    public void FormatAnswerPrompt_includes_question_text_and_id()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var answered = DateTime.SpecifyKind(new DateTime(2026, 1, 2), DateTimeKind.Utc);
        var choices = new DysonUserQuestion(
            id, "Pick one", ["A", "B"], false, true,
            new DysonUserQuestionAnswer(["A", "B"], null, answered));
        Assert.Equal(
            "Answer to question #7 \"Pick one\": A, B",
            DysonUserQuestion.FormatAnswerPrompt(choices, 7));
        Assert.Equal("Answer: A, B", DysonUserQuestion.FormatVisibleInstruction(choices));

        var custom = choices with
        {
            Answer = new DysonUserQuestionAnswer([], "my text", answered),
        };
        Assert.Equal(
            "Answer to question #7 \"Pick one\": my text",
            DysonUserQuestion.FormatAnswerPrompt(custom, 7));
        Assert.Equal("Answer: my text", DysonUserQuestion.FormatVisibleInstruction(custom));

        var both = choices with
        {
            Answer = new DysonUserQuestionAnswer(["A"], "note", answered),
        };
        Assert.Equal(
            "Answer to question #7 \"Pick one\": A; Other: note",
            DysonUserQuestion.FormatAnswerPrompt(both, 7));
        Assert.Equal("Answer: A; Other: note", DysonUserQuestion.FormatVisibleInstruction(both));

        var fallback = DysonUserQuestion.FormatAnswerPrompt(choices, null);
        Assert.Contains($"#{id:D}", fallback, StringComparison.Ordinal);
        Assert.StartsWith($"Answer to question #{id:D} \"Pick one\": A, B", fallback, StringComparison.Ordinal);

        var messy = choices with { Question = "Say \"hi\"\r\nnow" };
        Assert.Equal(
            "Answer to question #7 \"Say hi now\": A, B",
            DysonUserQuestion.FormatAnswerPrompt(messy, 7));
    }

    [Fact]
    public async Task Submit_from_modal_and_inline_cannot_both_record()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var posted = session.AppendDisplayInfoTurn("body", userQuestion: Sample(multi: false, allowCustom: true));
        var id = posted.UserQuestion!.Id;
        var first = session.TryRecordUserQuestionAnswer(id, ["A"], null);
        Assert.False(first.IsError, first.Error);
        var second = session.TryRecordUserQuestionAnswer(id, ["B"], null);
        Assert.True(second.IsError);
        Assert.Equal("PostUserQuestion: already answered.", second.Error);

        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var created = await DysonTempDb.Sessions(accessor).CreateSessionAsync(new DysonSessionCreateRequest
            {
                AgentMode = DysonAgentModes.MetaAgent,
                SystemPromptSnapshot = "root",
                Title = "pending",
            });
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            var questions = DysonTempDb.PendingUserQuestions(accessor);
            var inserted = await questions.CreateAsync(new DysonPendingUserQuestion(
                0,
                created.Value,
                posted.Id,
                id,
                "body",
                "Pick",
                ["A", "B"],
                false,
                true,
                null,
                null,
                DateTime.UtcNow));
            Assert.True(inserted.IsSuccess, inserted.IsError ? inserted.Error : null);

            var deleted = await questions.DeleteByQuestionGuidAsync(id, created.Value);
            Assert.False(deleted.IsError, deleted.Error);
            var again = await questions.DeleteByQuestionGuidAsync(id, created.Value);
            Assert.False(again.IsError, again.Error);
            var count = await questions.CountAsync(created.Value);
            Assert.True(count.IsSuccess, count.IsError ? count.Error : null);
            Assert.Equal(0, count.Value);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static DysonUserQuestion Sample(bool multi, bool allowCustom) =>
        new(Guid.NewGuid(), "Pick", ["A", "B"], multi, allowCustom, null);

    private static string ValidArgs(string message) =>
        "{\"message\":" + JsonSerializer.Serialize(message) + ",\"question\":\"Pick\",\"choices\":[\"A\",\"B\"]}";

    private static Task<(StubSession Session, DysonWorkspaceToolExecutor Executor)> MetaExecutorAsync() =>
        MetaExecutorAsync(pending: null, persistenceId: Guid.Empty);

    private static async Task<(StubSession Session, DysonWorkspaceToolExecutor Executor)> MetaExecutorAsync(
        IDysonPendingUserQuestionRepository? pending,
        Guid persistenceId)
    {
        var session = new StubSession(DysonAgentModes.MetaAgent, pending, persistenceId);
        var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        foreach (var name in new[] { "ListUserQuestions", "UpdateUserQuestion", "DeleteUserQuestion" })
            session.McpPipeline.Tools[name] = meta.Tools[name];
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), new HttpClient());
        return (session, executor);
    }

    [Fact]
    public async Task Update_replaces_the_card_and_the_pending_row()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessionId = await NewSessionAsync(accessor);
            var repo = DysonTempDb.PendingUserQuestions(accessor);
            var (session, executor) = await MetaExecutorAsync(repo, sessionId);
            var posted = await ExecuteAsync(executor, ValidArgs("Old body"));
            var id = QuestionId(posted);
            var turnId = Assert.Single(session.Turns).Id;
            await DysonPendingUserQuestionHooks.InsertAsync(session, session.Turns[0]);
            var before = (await repo.ListAsync(sessionId)).Value[0];

            var updated = await ExecuteNamedAsync(
                executor,
                "UpdateUserQuestion",
                "{\"questionId\":\"" + id.ToString("D") + "\",\"message\":\"New body\",\"question\":\"New title\","
                + "\"choices\":[\"Yes\",\"No\"],\"multiSelect\":true,\"allowCustomAnswer\":false,"
                + "\"actions\":[{\"name\":\"Open\",\"func\":\"open_plan:1\"}]}");
            Assert.False(updated.IsError, updated.Content);
            Assert.Contains(id.ToString("D"), updated.Content, StringComparison.Ordinal);

            var bubble = Assert.Single(session.Turns);
            Assert.Equal(turnId, bubble.Id);
            Assert.Equal("New body", bubble.AssistantText);
            Assert.Equal(id, bubble.UserQuestion!.Id);
            Assert.Equal("New title", bubble.UserQuestion.Question);
            Assert.Equal(["Yes", "No"], bubble.UserQuestion.Choices);
            Assert.True(bubble.UserQuestion.MultiSelect);
            Assert.False(bubble.UserQuestion.AllowCustomAnswer);
            Assert.Null(bubble.UserQuestion.Answer);
            Assert.Equal("open_plan:1", Assert.Single(bubble.ConversationActions).FuncKey);

            var row = (await repo.ListAsync(sessionId)).Value[0];
            Assert.Equal("New body", row.Message);
            Assert.Equal("New title", row.Question);
            Assert.Equal(["Yes", "No"], row.Choices);
            Assert.True(row.MultiSelect);
            Assert.False(row.AllowCustomAnswer);
            Assert.Equal(before.CreatedUtc, row.CreatedUtc);
            Assert.Equal(before.Kind, row.Kind);
            Assert.Equal(before.Id, row.Id);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    [Fact]
    public async Task Delete_clears_the_card_and_the_pending_row()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessionId = await NewSessionAsync(accessor);
            var repo = DysonTempDb.PendingUserQuestions(accessor);
            var (session, executor) = await MetaExecutorAsync(repo, sessionId);
            var id = QuestionId(await ExecuteAsync(executor, ValidArgs("Keep the bubble")));
            await DysonPendingUserQuestionHooks.InsertAsync(session, session.Turns[0]);

            var deleted = await ExecuteNamedAsync(
                executor, "DeleteUserQuestion", "{\"questionId\":\"" + id.ToString("D") + "\"}");
            Assert.False(deleted.IsError, deleted.Content);

            var bubble = Assert.Single(session.Turns);
            Assert.Null(bubble.UserQuestion);
            Assert.EndsWith("Withdrawn.", bubble.AssistantText, StringComparison.Ordinal);
            Assert.Equal(0, (await repo.CountAsync(sessionId)).Value);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    [Fact]
    public async Task Update_and_delete_refuse_an_answered_card()
    {
        var (session, executor) = await MetaExecutorAsync();
        var id = QuestionId(await ExecuteAsync(executor, ValidArgs("body")));
        Assert.False(session.TryRecordUserQuestionAnswer(id, ["A"], null).IsError);
        var before = session.Turns[0].UserQuestion;

        var updated = await ExecuteNamedAsync(executor, "UpdateUserQuestion", UpdateArgs(id));
        Assert.Equal("UpdateUserQuestion: already answered.", updated.Content);
        var deleted = await ExecuteNamedAsync(
            executor, "DeleteUserQuestion", "{\"questionId\":\"" + id.ToString("D") + "\"}");
        Assert.Equal("DeleteUserQuestion: already answered.", deleted.Content);
        Assert.Equal(before, session.Turns[0].UserQuestion);
    }

    [Fact]
    public async Task Unknown_question_id_is_not_found()
    {
        var (session, executor) = await MetaExecutorAsync();
        var id = Guid.NewGuid();
        var updated = await ExecuteNamedAsync(executor, "UpdateUserQuestion", UpdateArgs(id));
        Assert.Equal("UpdateUserQuestion: not found.", updated.Content);
        var deleted = await ExecuteNamedAsync(
            executor, "DeleteUserQuestion", "{\"questionId\":\"" + id.ToString("D") + "\"}");
        Assert.Equal("DeleteUserQuestion: not found.", deleted.Content);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public async Task A_question_from_another_session_is_not_found()
    {
        var (other, otherExecutor) = await MetaExecutorAsync();
        var id = QuestionId(await ExecuteAsync(otherExecutor, ValidArgs("theirs")));
        var snapshot = other.Turns[0].UserQuestion;

        var (session, executor) = await MetaExecutorAsync();
        var updated = await ExecuteNamedAsync(executor, "UpdateUserQuestion", UpdateArgs(id));
        Assert.Equal("UpdateUserQuestion: not found.", updated.Content);
        Assert.Empty(session.Turns);
        Assert.Equal(snapshot, other.Turns[0].UserQuestion);
    }

    [Fact]
    public async Task Secure_data_id_is_refused()
    {
        var (session, executor) = await MetaExecutorAsync();
        var requestId = Guid.NewGuid();
        session.AppendDisplayInfoTurn(
            "Enter secrets",
            secureDataRequest: new DysonSecureDataRequest(
                requestId,
                [new DysonSecureDataField("api_key", "The API key")],
                Outcome: null));

        var updated = await ExecuteNamedAsync(executor, "UpdateUserQuestion", UpdateArgs(requestId));
        Assert.Equal(
            "UpdateUserQuestion: that id is a secure data request. Use RequestSecureData.",
            updated.Content);
        var deleted = await ExecuteNamedAsync(
            executor, "DeleteUserQuestion", "{\"questionId\":\"" + requestId.ToString("D") + "\"}");
        Assert.Equal(
            "DeleteUserQuestion: that id is a secure data request. Use RequestSecureData.",
            deleted.Content);
        Assert.Equal(requestId, session.Turns[0].SecureDataRequest!.Id);
        Assert.Null(session.Turns[0].SecureDataRequest.Outcome);
    }

    [Fact]
    public async Task Update_and_delete_and_list_reject_when_not_meta_agent()
    {
        using var http = new HttpClient();
        var work = new StubSession(DysonAgentModes.Work);
        var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
        foreach (var name in new[] { "ListUserQuestions", "UpdateUserQuestion", "DeleteUserQuestion" })
            work.McpPipeline.Tools[name] = meta.Tools[name];
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, Path.GetTempPath(), http);

        var listed = await ExecuteNamedAsync(executor, "ListUserQuestions", "{}");
        Assert.Equal("ListUserQuestions is only available in Meta Agent mode.", listed.Content);
        var updated = await ExecuteNamedAsync(executor, "UpdateUserQuestion", UpdateArgs(Guid.NewGuid()));
        Assert.Equal("UpdateUserQuestion is only available in Meta Agent mode.", updated.Content);
        var deleted = await ExecuteNamedAsync(
            executor, "DeleteUserQuestion", "{\"questionId\":\"" + Guid.NewGuid().ToString("D") + "\"}");
        Assert.Equal("DeleteUserQuestion is only available in Meta Agent mode.", deleted.Content);
        Assert.Empty(work.Turns);
    }

    [Fact]
    public async Task Answer_and_replace_cannot_both_win()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var posted = session.AppendDisplayInfoTurn("body", userQuestion: Sample(multi: false, allowCustom: true));
        var id = posted.UserQuestion!.Id;
        var replacement = new DysonUserQuestion(id, "New", ["Yes", "No"], false, true, null);

        var gate = new Barrier(2);
        Result<DysonUserQuestionAnswer, string> answered = default;
        VoidResult<string> replaced = default;
        var answerThread = new Thread(() =>
        {
            gate.SignalAndWait();
            answered = session.TryRecordUserQuestionAnswer(id, ["A"], null);
        });
        var replaceThread = new Thread(() =>
        {
            gate.SignalAndWait();
            replaced = session.TryReplaceUserQuestion(id, replacement, "New body", null);
        });
        answerThread.Start();
        replaceThread.Start();
        Assert.True(answerThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(replaceThread.Join(TimeSpan.FromSeconds(5)));

        var winners = (answered.IsSuccess ? 1 : 0) + (replaced.IsSuccess ? 1 : 0);
        Assert.Equal(1, winners);
        var card = session.Turns[0].UserQuestion!;
        if (answered.IsSuccess)
        {
            Assert.NotNull(card.Answer);
            Assert.Equal(["A", "B"], card.Choices);
        }
        else
        {
            Assert.Null(card.Answer);
            Assert.Equal(["Yes", "No"], card.Choices);
        }
    }

    [Fact]
    public async Task Update_validation_keeps_the_post_prefix_intact()
    {
        var (session, executor) = await MetaExecutorAsync();
        var id = QuestionId(await ExecuteAsync(executor, ValidArgs("body")));
        var before = session.Turns[0].UserQuestion;

        var one = await ExecuteNamedAsync(
            executor,
            "UpdateUserQuestion",
            "{\"questionId\":\"" + id.ToString("D") + "\",\"message\":\"body\",\"question\":\"Pick\",\"choices\":[\"A\"]}");
        Assert.Equal("UpdateUserQuestion: choices require at least 2.", one.Content);
        Assert.Equal(before, session.Turns[0].UserQuestion);

        var post = await ExecuteAsync(executor, """{"message":"body","question":"Pick","choices":["A"]}""");
        Assert.Equal("PostUserQuestion: choices require at least 2.", post.Content);
    }

    [Fact]
    public async Task List_returns_pending_only_unless_includeAnswered()
    {
        var (session, executor) = await MetaExecutorAsync();
        var open = QuestionId(await ExecuteAsync(executor, ValidArgs("open")));
        var answered = QuestionId(await ExecuteAsync(executor, ValidArgs("answered")));
        Assert.False(session.TryRecordUserQuestionAnswer(answered, ["A"], null).IsError);
        var withdrawn = QuestionId(await ExecuteAsync(executor, ValidArgs("withdrawn")));
        Assert.False((await ExecuteNamedAsync(
            executor, "DeleteUserQuestion", "{\"questionId\":\"" + withdrawn.ToString("D") + "\"}")).IsError);
        session.AppendDisplayInfoTurn(
            "Enter secrets",
            secureDataRequest: new DysonSecureDataRequest(
                Guid.NewGuid(),
                [new DysonSecureDataField("k", "label")],
                Outcome: null));

        var pending = await ExecuteNamedAsync(executor, "ListUserQuestions", "{}");
        Assert.False(pending.IsError, pending.Content);
        using var doc = JsonDocument.Parse(pending.Content);
        var rows = doc.RootElement.GetProperty("questions");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(open.ToString("D"), rows[0].GetProperty("questionId").GetString());
        Assert.Equal("Pick", rows[0].GetProperty("question").GetString());
        Assert.Equal("pending", rows[0].GetProperty("state").GetString());
        Assert.False(rows[0].GetProperty("multiSelect").GetBoolean());
        Assert.Equal(2, rows[0].GetProperty("choices").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(rows[0].GetProperty("postedUtc").GetString()));
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());

        var all = await ExecuteNamedAsync(executor, "ListUserQuestions", "{\"includeAnswered\":true}");
        using var allDoc = JsonDocument.Parse(all.Content);
        var allRows = allDoc.RootElement.GetProperty("questions");
        Assert.Equal(3, allRows.GetArrayLength());
        Assert.Equal("pending", allRows[0].GetProperty("state").GetString());
        Assert.Equal("answered", allRows[1].GetProperty("state").GetString());
        Assert.Equal("withdrawn", allRows[2].GetProperty("state").GetString());
        Assert.Equal(0, allRows[2].GetProperty("choices").GetArrayLength());
        Assert.Equal(all.Content, (await ExecuteNamedAsync(executor, "ListUserQuestions", "{\"includeAnswered\":true}")).Content);
    }

    [Fact]
    public async Task List_empty_and_answered_only_are_an_empty_array()
    {
        var (session, executor) = await MetaExecutorAsync();
        var empty = await ExecuteNamedAsync(executor, "ListUserQuestions", "{}");
        Assert.Equal("{\"questions\":[]}", empty.Content);

        QuestionId(await ExecuteAsync(executor, ValidArgs("done")));
        Assert.False(session.TryRecordUserQuestionAnswer(session.Turns[0].UserQuestion!.Id, ["A"], null).IsError);
        var answeredOnly = await ExecuteNamedAsync(executor, "ListUserQuestions", "{}");
        Assert.Equal("{\"questions\":[]}", answeredOnly.Content);
    }

    [Fact]
    public async Task List_caps_at_fifty_oldest()
    {
        var (session, executor) = await MetaExecutorAsync();
        for (var i = 0; i < 51; i++)
        {
            var turn = session.AppendDisplayInfoTurn(
                "body " + i,
                userQuestion: new DysonUserQuestion(Guid.NewGuid(), "Q" + i, ["A", "B"], false, true, null));
            turn.StartedUtc = DateTime.UnixEpoch.AddMinutes(i);
        }

        var listed = await ExecuteNamedAsync(executor, "ListUserQuestions", "{}");
        using var doc = JsonDocument.Parse(listed.Content);
        var rows = doc.RootElement.GetProperty("questions");
        Assert.Equal(50, rows.GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("Q0", rows[0].GetProperty("question").GetString());
        Assert.Equal("Q49", rows[49].GetProperty("question").GetString());
    }

    [Fact]
    public async Task List_does_not_write()
    {
        var (session, executor) = await MetaExecutorAsync();
        QuestionId(await ExecuteAsync(executor, ValidArgs("body")));
        var updates = 0;
        session.TurnUpdated += (_, _) => updates++;
        var before = session.Turns.Count;

        Assert.False((await ExecuteNamedAsync(executor, "ListUserQuestions", "{}")).IsError);
        Assert.Equal(before, session.Turns.Count);
        Assert.NotNull(session.Turns[0].UserQuestion);
        Assert.Equal(0, updates);
    }

    private static Guid QuestionId(DysonToolCallResult result)
    {
        Assert.False(result.IsError, result.Content);
        using var doc = JsonDocument.Parse(result.Content);
        return Guid.Parse(doc.RootElement.GetProperty("questionId").GetString()!);
    }

    private static string UpdateArgs(Guid id) =>
        "{\"questionId\":\"" + id.ToString("D") + "\",\"message\":\"next\",\"question\":\"Next\",\"choices\":[\"Yes\",\"No\"]}";

    private static async Task<Guid> NewSessionAsync(DysonDbAccessor accessor)
    {
        var created = await DysonTempDb.Sessions(accessor).CreateSessionAsync(new DysonSessionCreateRequest
        {
            AgentMode = DysonAgentModes.MetaAgent,
            SystemPromptSnapshot = "root",
            Title = "questions",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        return created.Value;
    }

    private static void DeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static Task<DysonToolCallResult> ExecuteAsync(
        DysonWorkspaceToolExecutor executor,
        string argumentsJson) =>
        ExecuteNamedAsync(executor, "PostUserQuestion", argumentsJson);

    private static Task<DysonToolCallResult> ExecuteNamedAsync(
        DysonWorkspaceToolExecutor executor,
        string toolName,
        string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = toolName,
            Stage = 1,
            ArgumentsJson = argumentsJson,
        });

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(
            string mode,
            IDysonPendingUserQuestionRepository? pending = null,
            Guid persistenceId = default)
            : base(mode, new DysonAgentSessionConfig { PendingUserQuestions = pending }, new StubProvider())
        {
            if (persistenceId != Guid.Empty)
                PersistenceId = persistenceId;
        }

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

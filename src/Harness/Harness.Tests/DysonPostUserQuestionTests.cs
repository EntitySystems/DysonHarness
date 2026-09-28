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

        var work = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.Work);
        Assert.False(work.Tools.ContainsKey("PostUserQuestion"));

        var drone = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig(),
            DysonAgentModes.MetaAgentDrone,
            interAgentDepth: 1,
            omitRootTaskCompletionTools: true);
        Assert.False(drone.Tools.ContainsKey("PostUserQuestion"));
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

    private static DysonUserQuestion Sample(bool multi, bool allowCustom) =>
        new(Guid.NewGuid(), "Pick", ["A", "B"], multi, allowCustom, null);

    private static string ValidArgs(string message) =>
        "{\"message\":" + JsonSerializer.Serialize(message) + ",\"question\":\"Pick\",\"choices\":[\"A\",\"B\"]}";

    private static async Task<(StubSession Session, DysonWorkspaceToolExecutor Executor)> MetaExecutorAsync()
    {
        var session = new StubSession(DysonAgentModes.MetaAgent);
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), new HttpClient());
        return (session, executor);
    }

    private static Task<DysonToolCallResult> ExecuteAsync(
        DysonWorkspaceToolExecutor executor,
        string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = "PostUserQuestion",
            Stage = 1,
            ArgumentsJson = argumentsJson,
        });

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

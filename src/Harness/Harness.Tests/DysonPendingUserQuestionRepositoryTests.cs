using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: ulong id round-trip, subject/session isolation, missing delete is success.
/// </summary>
public class DysonPendingUserQuestionRepositoryTests
{
    [Fact]
    public async Task PendingUserQuestion_ulong_id_increments_and_roundtrips()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessions = DysonTempDb.Sessions(accessor);
            var questions = DysonTempDb.PendingUserQuestions(accessor);
            var sessionId = await CreateSessionAsync(sessions, "ulong");

            var first = await questions.CreateAsync(Question(sessionId, ["  Yes ", "", "No", "Yes"]));
            AssertOk(first);
            var second = await questions.CreateAsync(Question(sessionId, ["Later"]));
            AssertOk(second);

            Assert.True(first.Value > 0);
            Assert.Equal(first.Value + 1, second.Value);

            var listed = await questions.ListAsync(sessionId);
            AssertOk(listed);
            Assert.Equal(2, listed.Value.Count);
            Assert.Equal(first.Value, listed.Value[0].Id);
            Assert.Equal(second.Value, listed.Value[1].Id);
            Assert.Equal(["Yes", "No"], listed.Value[0].Choices);
            Assert.Equal(["Later"], listed.Value[1].Choices);
            Assert.Equal(DateTimeKind.Utc, listed.Value[0].CreatedUtc.Kind);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    [Fact]
    public async Task Create_List_Delete_filters_by_subject_and_session()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var subject = DysonTempDb.Subject();
            var sessions = DysonTempDb.Sessions(accessor, subject);
            var questions = DysonTempDb.PendingUserQuestions(accessor, subject);

            var sessionA = await CreateSessionAsync(sessions, "A");
            var sessionB = await CreateSessionAsync(sessions, "B");
            var card = Guid.NewGuid();

            var created = await questions.CreateAsync(Question(sessionA, ["One"], card));
            AssertOk(created);
            var other = await questions.CreateAsync(Question(sessionB, ["Two"]));
            AssertOk(other);

            var listA = await questions.ListAsync(sessionA);
            AssertOk(listA);
            Assert.Equal(created.Value, Assert.Single(listA.Value).Id);

            var listB = await questions.ListAsync(sessionB);
            AssertOk(listB);
            Assert.Equal(other.Value, Assert.Single(listB.Value).Id);

            var countA = await questions.CountAsync(sessionA);
            AssertOk(countA);
            Assert.Equal(1, countA.Value);

            var missing = await questions.DeleteAsync(created.Value + 100, sessionA);
            AssertVoidOk(missing);

            var still = await questions.CountAsync(sessionA);
            AssertOk(still);
            Assert.Equal(1, still.Value);

            subject.SubjectId = "other-subject";
            var foreignList = await questions.ListAsync(sessionA);
            Assert.True(foreignList.IsError);
            Assert.Equal($"Session '{sessionA}' not found.", foreignList.Error);

            var foreignDelete = await questions.DeleteAsync(created.Value, sessionA);
            Assert.True(foreignDelete.IsError);
            Assert.Equal($"Session '{sessionA}' not found.", foreignDelete.Error);

            var foreignGuid = await questions.DeleteByQuestionGuidAsync(card, sessionA);
            Assert.True(foreignGuid.IsError);

            subject.SubjectId = DysonSubjects.Local;
            var deleted = await questions.DeleteByQuestionGuidAsync(card, sessionA);
            AssertVoidOk(deleted);
            var again = await questions.DeleteByQuestionGuidAsync(card, sessionA);
            AssertVoidOk(again);

            var empty = await questions.ListAsync(sessionA);
            AssertOk(empty);
            Assert.Empty(empty.Value);

            var survivor = await questions.ListAsync(sessionB);
            AssertOk(survivor);
            Assert.Equal(other.Value, Assert.Single(survivor.Value).Id);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    private static DysonPendingUserQuestion Question(
        Guid sessionId,
        IReadOnlyList<string> choices,
        Guid? questionGuid = null) =>
        new(
            Id: 99,
            SessionId: sessionId,
            TurnId: Guid.NewGuid(),
            QuestionGuid: questionGuid ?? Guid.NewGuid(),
            Message: "  body  ",
            Question: "  title  ",
            Choices: choices,
            MultiSelect: false,
            AllowCustomAnswer: true,
            SubagentId: null,
            EventId: null,
            CreatedUtc: DateTime.UnixEpoch);

    private static async Task<Guid> CreateSessionAsync(DysonSessionRepository sessions, string title)
    {
        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.Work,
            SystemPromptSnapshot = "test",
            Title = title,
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

    private static void AssertVoidOk(VoidResult<string> result) =>
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);

    private static void AssertOk<T>(Result<T, string> result) =>
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
}

using System.Text;
using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Plan 230: secrets live in <c>session_secrets</c> only. Every Fact that can see a value checks it never
/// reaches a turn, hidden instruction, card JSON, pending row, or tool result.
/// </summary>
public class DysonSessionSecretTests
{
    private const string Secret = "s3cr3t-line1\nline2-ZQX";

    [Fact]
    public async Task Repo_upsert_overwrites_lists_keys_once_and_delete_is_idempotent()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessionId = await CreateSessionAsync(accessor);
            var secrets = DysonTempDb.SessionSecrets(accessor);

            AssertOk(await secrets.UpsertAsync(sessionId, "api_key", "API key", "old"));
            AssertOk(await secrets.UpsertAsync(sessionId, "api_key", "API key", Secret));
            AssertOk(await secrets.UpsertAsync(sessionId, "a.token", "Token", "t"));

            var keys = await secrets.ListKeysAsync(sessionId);
            Assert.True(keys.IsSuccess, keys.IsError ? keys.Error : null);
            Assert.Equal(["a.token", "api_key"], keys.Value);

            var value = await secrets.GetAsync(sessionId, "api_key");
            Assert.True(value.IsSuccess, value.IsError ? value.Error : null);
            Assert.Equal(Secret, value.Value);

            var badKey = await secrets.UpsertAsync(sessionId, "bad key", "d", "v");
            Assert.True(badKey.IsError);
            var tooLong = await secrets.UpsertAsync(sessionId, "k", "d", new string('x', 8193));
            Assert.True(tooLong.IsError);
            Assert.DoesNotContain("xxxx", tooLong.Error, StringComparison.Ordinal);

            AssertOk(await secrets.DeleteAsync(sessionId, "api_key"));
            AssertOk(await secrets.DeleteAsync(sessionId, "api_key"));
            Assert.True((await secrets.GetAsync(sessionId, "api_key")).IsError);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    [Fact]
    public void Write_file_bytes_are_exact_utf8_without_bom_or_trailing_newline()
    {
        var root = Guid.NewGuid();
        var written = DysonSecureDataFiles.Write(root, "api_key", Secret);
        Assert.True(written.IsSuccess, written.IsError ? written.Error : null);
        try
        {
            Assert.EndsWith(DysonSecureDataFiles.Extension, written.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, written.Value, StringComparison.Ordinal);
            Assert.Equal(Encoding.UTF8.GetBytes(Secret), File.ReadAllBytes(written.Value));
            Assert.Equal([written.Value], DysonSecureDataFiles.TrackedPaths(root, "api_key"));

            DysonSecureDataFiles.DeleteFor(root, "api_key");
            Assert.False(File.Exists(written.Value));
            Assert.Empty(DysonSecureDataFiles.TrackedPaths(root, "api_key"));
            DysonSecureDataFiles.DeleteFor(root, "api_key");
        }
        finally
        {
            if (File.Exists(written.Value))
                File.Delete(written.Value);
        }
    }

    [Fact]
    public void SweepStale_deletes_only_old_secret_files()
    {
        var dir = Directory.CreateTempSubdirectory("dyson-secret-sweep-").FullName;
        try
        {
            var old = Path.Combine(dir, "a" + DysonSecureDataFiles.Extension);
            var fresh = Path.Combine(dir, "b" + DysonSecureDataFiles.Extension);
            var other = Path.Combine(dir, "c.txt");
            File.WriteAllText(old, "x");
            File.WriteAllText(fresh, "x");
            File.WriteAllText(other, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-8));

            Assert.Equal(1, DysonSecureDataFiles.SweepStale(directory: dir));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Answer_texts_list_keys_only()
    {
        var id = Guid.NewGuid();
        var at = DateTime.UtcNow;
        var both = new DysonSecureDataOutcome(false, ["a", "b"], [], at);
        Assert.Equal("Secrets stored for keys: a, b.", DysonSecureDataRequest.FormatAnswerPrompt(both));
        var partial = new DysonSecureDataOutcome(false, ["a"], ["b"], at);
        Assert.Equal("Secrets stored for keys: a. Skipped: b.", DysonSecureDataRequest.FormatAnswerPrompt(partial));
        var cancelled = new DysonSecureDataOutcome(true, [], ["a"], at);
        Assert.Equal("Secrets request cancelled. No keys stored.", DysonSecureDataRequest.FormatAnswerPrompt(cancelled));
        var blank = new DysonSecureDataOutcome(false, [], ["a"], at);
        Assert.Equal("Secrets request submitted. No keys stored. Skipped: a.", DysonSecureDataRequest.FormatAnswerPrompt(blank));

        var hidden = DysonSecureDataRequest.FormatHiddenInstruction(id, partial);
        Assert.Contains($"- requestId: {id:D}", hidden, StringComparison.Ordinal);
        Assert.Contains("- stored: a", hidden, StringComparison.Ordinal);
        Assert.Contains("- skipped: b", hidden, StringComparison.Ordinal);
        Assert.StartsWith("Secure data request (cancelled by the user):",
            DysonSecureDataRequest.FormatHiddenInstruction(id, cancelled), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOutcome_skips_blank_and_rejects_unknown_and_oversized()
    {
        var request = Request("a", "b");
        var built = DysonSecureDataRequest.BuildOutcome(
            request, new Dictionary<string, string> { ["a"] = Secret, ["b"] = "" }, false, DateTime.UtcNow);
        Assert.True(built.IsSuccess, built.IsError ? built.Error : null);
        Assert.Equal(["a"], built.Value.Stored);
        Assert.Equal(["b"], built.Value.Skipped);

        var unknown = DysonSecureDataRequest.BuildOutcome(
            request, new Dictionary<string, string> { ["zzz"] = Secret }, false, DateTime.UtcNow);
        Assert.True(unknown.IsError);
        Assert.DoesNotContain(Secret, unknown.Error, StringComparison.Ordinal);

        var big = new string('q', 8193);
        var over = DysonSecureDataRequest.BuildOutcome(
            request, new Dictionary<string, string> { ["a"] = big }, false, DateTime.UtcNow);
        Assert.True(over.IsError);
        Assert.DoesNotContain("qqqq", over.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Serializer_round_trips_and_rejects_bad_json()
    {
        var request = Request("a", "b") with
        {
            Outcome = new DysonSecureDataOutcome(false, ["a"], ["b"], DateTime.UtcNow),
        };
        var json = DysonSecureDataRequestSerializer.Serialize(request);
        var back = DysonSecureDataRequestSerializer.Deserialize(json);
        Assert.NotNull(back);
        Assert.Equal(request.Id, back!.Id);
        Assert.Equal(request.Fields, back.Fields);
        Assert.Equal(["a"], back.Outcome!.Stored);
        Assert.Equal(DateTimeKind.Utc, back.Outcome.CompletedUtc.Kind);
        Assert.Null(DysonSecureDataRequestSerializer.Deserialize("{"));
        Assert.Null(DysonSecureDataRequestSerializer.Deserialize(" "));

        var field = new DysonSecureDataField("k", "Desc");
        Assert.Equal(field, DysonSecureDataRequest.DecodePendingChoice(DysonSecureDataRequest.EncodePendingChoice(field)));
        Assert.Null(DysonSecureDataRequest.DecodePendingChoice("nope"));
    }

    [Fact]
    public async Task Request_submit_list_write_delete_never_leak_the_value()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        string? filePath = null;
        try
        {
            var sessionId = await CreateSessionAsync(accessor);
            var pending = DysonTempDb.PendingUserQuestions(accessor);
            var config = new DysonAgentSessionConfig
            {
                SessionSecrets = DysonTempDb.SessionSecrets(accessor),
                PendingUserQuestions = pending,
            };
            var session = new StubSession(DysonAgentModes.MetaAgent, config, sessionId);
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);

            var posted = await ExecuteAsync(
                executor,
                "RequestSecureData",
                """{"data":[{"key":"api_key","description":"  The   API key "},{"key":"other","description":"Other"}]}""");
            Assert.False(posted.IsError, posted.Content);
            using var doc = JsonDocument.Parse(posted.Content);
            var requestId = Guid.Parse(doc.RootElement.GetProperty("requestId").GetString()!);
            var card = Assert.Single(session.Turns);
            Assert.True(DysonPendingUserQuestionHooks.IsPendingQuestionTurn(card));
            Assert.Equal("The API key", card.SecureDataRequest!.Fields[0].Description);

            var inserted = await DysonPendingUserQuestionHooks.InsertAsync(session, card);
            Assert.True(inserted.IsSuccess, inserted.IsError ? inserted.Error : null);
            await pending.CreateAsync(new DysonPendingUserQuestion(
                0, sessionId, Guid.NewGuid(), Guid.NewGuid(), "body", "Pick", ["A", "B"],
                false, true, null, null, DateTime.UtcNow));
            var count = await pending.CountAsync(sessionId);
            Assert.Equal(2, count.Value);
            var rows = await pending.ListAsync(sessionId);
            var row = Assert.Single(rows.Value, r => r.Kind == DysonPendingUserQuestionKind.SecureData);
            Assert.Equal(requestId, row.QuestionGuid);
            Assert.Equal(DysonSecureDataRequest.PendingTitle, row.Question);
            Assert.Equal("api_key", DysonSecureDataRequest.DecodePendingChoice(row.Choices[0])!.Key);

            var recorded = await DysonSecureDataSubmission.RecordAsync(
                session, requestId, new Dictionary<string, string> { ["api_key"] = Secret, ["other"] = " " }, cancelled: false);
            Assert.True(recorded.IsSuccess, recorded.IsError ? recorded.Error : null);
            Assert.Equal(["api_key"], recorded.Value.Stored);
            Assert.Equal(["other"], recorded.Value.Skipped);

            var again = await DysonSecureDataSubmission.RecordAsync(
                session, requestId, new Dictionary<string, string> { ["api_key"] = "x" }, cancelled: false);
            Assert.True(again.IsError);

            var answer = DysonSecureDataSubmission.CreateAnswerTurn(requestId, recorded.Value);
            Assert.Equal("Secrets stored for keys: api_key. Skipped: other.", answer.Instruction);

            var listed = await ExecuteAsync(executor, "ListSecureDataKeys", "{}");
            Assert.False(listed.IsError, listed.Content);
            Assert.Equal("""{"keys":["api_key"]}""", listed.Content);

            var written = await ExecuteAsync(executor, "WriteSecureDataToFile", """{"key":"api_key"}""");
            Assert.False(written.IsError, written.Content);
            using (var w = JsonDocument.Parse(written.Content))
                filePath = w.RootElement.GetProperty("path").GetString();
            Assert.Equal(Encoding.UTF8.GetBytes(Secret), File.ReadAllBytes(filePath!));

            var missing = await ExecuteAsync(executor, "WriteSecureDataToFile", """{"key":"nope"}""");
            Assert.True(missing.IsError);

            var deleted = await ExecuteAsync(executor, "DeleteSecureData", """{"key":"api_key"}""");
            Assert.False(deleted.IsError, deleted.Content);
            Assert.False(File.Exists(filePath));
            var deletedAgain = await ExecuteAsync(executor, "DeleteSecureData", """{"key":"api_key"}""");
            Assert.False(deletedAgain.IsError, deletedAgain.Content);
            Assert.Equal("""{"keys":[]}""", (await ExecuteAsync(executor, "ListSecureDataKeys", "{}")).Content);

            var entity = DysonTurnPersistence.ToEntity(card, sessionId, sequence: 1);
            var haystack = string.Join(
                "\u0001",
                posted.Content, listed.Content, written.Content, missing.Content, deleted.Content,
                again.IsError ? again.Error : "",
                answer.Instruction, answer.HiddenInstruction, entity.SecureDataRequestJson,
                row.Message, row.Question, string.Join("|", row.Choices),
                string.Join("|", session.Turns.Select(t => t.AssistantText + t.Instruction + t.HiddenInstruction)),
                string.Join("\n", session.SnapshotLog()));
            Assert.DoesNotContain("s3cr3t", haystack, StringComparison.Ordinal);
            Assert.DoesNotContain("ZQX", haystack, StringComparison.Ordinal);
            Assert.Contains("api_key", entity.SecureDataRequestJson, StringComparison.Ordinal);
        }
        finally
        {
            if (filePath is not null && File.Exists(filePath))
                File.Delete(filePath);
            DeleteTemp(path);
        }
    }

    [Fact]
    public async Task Cancel_stores_nothing_and_work_mode_is_rejected()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var sessionId = await CreateSessionAsync(accessor);
            var secrets = DysonTempDb.SessionSecrets(accessor);
            var config = new DysonAgentSessionConfig { SessionSecrets = secrets };
            var session = new StubSession(DysonAgentModes.MetaAgent, config, sessionId);
            var card = session.AppendDisplayInfoTurn("Enter secrets", secureDataRequest: Request("a"));

            var cancelled = await DysonSecureDataSubmission.RecordAsync(
                session, card.SecureDataRequest!.Id, new Dictionary<string, string> { ["a"] = Secret }, cancelled: true);
            Assert.True(cancelled.IsSuccess, cancelled.IsError ? cancelled.Error : null);
            Assert.True(cancelled.Value.Cancelled);
            Assert.Empty((await secrets.ListKeysAsync(sessionId)).Value);
            Assert.False(DysonPendingUserQuestionHooks.IsPendingQuestionTurn(card));

            var work = new StubSession(DysonAgentModes.Work, config, sessionId);
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(work, Path.GetTempPath(), http);
            var listed = await ExecuteAsync(executor, "ListSecureDataKeys", "{}");
            Assert.True(listed.IsError);
        }
        finally
        {
            DeleteTemp(path);
        }
    }

    private static DysonSecureDataRequest Request(params string[] keys) =>
        new(Guid.NewGuid(), keys.Select(k => new DysonSecureDataField(k, "Desc " + k)).ToList(), null);

    private static async Task<Guid> CreateSessionAsync(DysonDbAccessor accessor)
    {
        var created = await DysonTempDb.Sessions(accessor).CreateSessionAsync(new DysonSessionCreateRequest
        {
            RuntimeId = 1,
            AgentMode = DysonAgentModes.MetaAgent,
            SystemPromptSnapshot = "root",
            Title = "secrets",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        return created.Value;
    }

    private static Task<DysonToolCallResult> ExecuteAsync(
        DysonWorkspaceToolExecutor executor,
        string tool,
        string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Stage = 1,
            ArgumentsJson = argumentsJson,
        });

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

    private static void AssertOk(VoidResult<string> result) =>
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(string mode, DysonAgentSessionConfig config, Guid persistenceId)
            : base(mode, config, new StubProvider()) =>
            PersistenceId = persistenceId;

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

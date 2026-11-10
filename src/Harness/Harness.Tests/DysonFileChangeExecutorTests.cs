using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: real temp workspace, temp DB and bus. The event is awaited with a timeout guard, not a sleep.
/// </summary>
public class DysonFileChangeExecutorTests
{
    [Fact]
    public async Task WriteFile_edit_records_one_row_and_one_event()
    {
        await using var fx = await Fixture.CreateAsync();
        var child = await fx.ChildAsync(depth: 2);
        File.WriteAllText(Path.Combine(fx.Root, "note.txt"), "aaa\nbbb\nccc\n");

        var waited = WaitForChange(fx.Bus, fx.RootId);
        var result = await CallAsync(await fx.Executor(child), "WriteFile", """{"path":"note.txt","old_text":"bbb","new_text":"BBB"}""");
        Assert.False(result.IsError, result.Content);

        var change = await waited;
        var rows = await fx.Changes.ListAsync(fx.RootId, null, 10);
        Assert.True(rows.IsSuccess, rows.IsError ? rows.Error : null);
        var row = Assert.Single(rows.Value);
        Assert.Equal(change.ChangeId, row.Id);
        Assert.Equal(DysonFileChangeKind.Edited, row.Kind);
        Assert.Equal(fx.RootId, row.RootSessionId);
        Assert.Equal(child.PersistenceId, row.SessionId);
        // Runtime ids are allocated per parent, so each level's first child is #1.
        Assert.Equal("#1->#1", row.AgentChain);
        Assert.Equal("WriteFile", row.ToolName);
        Assert.Equal("note.txt", row.RelativePath);
        Assert.Equal("bbb", row.RemovedText);
        Assert.Equal("BBB", row.AddedText);
        Assert.Equal(fx.WorkDirectoryId, row.WorkDirectoryId);
        Assert.DoesNotContain('\\', row.RelativePath);
    }

    [Fact]
    public async Task WriteFile_content_overwrites_and_CreateFile_creates()
    {
        await using var fx = await Fixture.CreateAsync();
        var session = fx.RootSession();
        File.WriteAllText(Path.Combine(fx.Root, "have.txt"), "old\n");

        var overwrite = await CallAsync(await fx.Executor(session), "WriteFile", """{"path":"have.txt","content":"new\n"}""");
        Assert.False(overwrite.IsError, overwrite.Content);
        var created = await CallAsync(await fx.Executor(session), "CreateFile", """{"path":"fresh.txt","content":"hello\n"}""");
        Assert.False(created.IsError, created.Content);

        var rows = await fx.Changes.ListAsync(fx.RootId, null, 10);
        Assert.True(rows.IsSuccess, rows.IsError ? rows.Error : null);
        Assert.Equal(2, rows.Value.Count);
        var create = rows.Value.Single(r => r.RelativePath == "fresh.txt");
        var over = rows.Value.Single(r => r.RelativePath == "have.txt");
        Assert.Equal(DysonFileChangeKind.Created, create.Kind);
        Assert.Equal("hello", create.AddedText);
        Assert.Equal(DysonFileChangeKind.Overwritten, over.Kind);
        Assert.Equal("old", over.RemovedText);
        Assert.Equal("new", over.AddedText);
        Assert.Equal("", create.AgentChain);
    }

    [Fact]
    public async Task Temp_file_and_secure_data_write_no_row()
    {
        await using var fx = await Fixture.CreateAsync();
        var session = fx.RootSession();
        session.Config.SessionSecrets = fx.Secrets;

        var temp = await CallAsync(await fx.Executor(session), "CreateFile", """{"path":"chart.html","content":"<p></p>","isTempFile":true}""");
        Assert.False(temp.IsError, temp.Content);

        var saved = await fx.Secrets.UpsertAsync(fx.RootId, "api_key", "description", "secret-value");
        Assert.True(saved.IsSuccess, saved.IsError ? saved.Error : null);
        var secure = await CallAsync(await fx.Executor(session), "WriteSecureDataToFile", """{"key":"api_key"}""");
        Assert.False(secure.IsError, secure.Content);

        var rows = await fx.Changes.ListAsync(fx.RootId, null, 10);
        Assert.True(rows.IsSuccess, rows.IsError ? rows.Error : null);
        Assert.Empty(rows.Value);
    }

    [Fact]
    public async Task Unpersisted_root_writes_no_row_and_the_tool_succeeds()
    {
        await using var fx = await Fixture.CreateAsync();
        var session = fx.RootSession(persisted: false);
        var result = await CallAsync(await fx.Executor(session), "CreateFile", """{"path":"x.txt","content":"x"}""");
        Assert.False(result.IsError, result.Content);

        var rows = await fx.Changes.ListAsync(fx.RootId, null, 10);
        Assert.True(rows.IsSuccess, rows.IsError ? rows.Error : null);
        Assert.Empty(rows.Value);
    }

    [Fact]
    public async Task Repository_failure_does_not_fail_the_tool()
    {
        await using var fx = await Fixture.CreateAsync();
        var session = fx.RootSession();
        session.Config.FileChanges = new FailingChanges();

        var result = await CallAsync(await fx.Executor(session), "CreateFile", """{"path":"y.txt","content":"y"}""");
        Assert.False(result.IsError, result.Content);
        Assert.Contains(session.SnapshotLog(), l => l.Contains("file change:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Identical_rewrite_writes_no_row()
    {
        await using var fx = await Fixture.CreateAsync();
        var session = fx.RootSession();
        File.WriteAllText(Path.Combine(fx.Root, "same.txt"), "same\r\n");

        var result = await CallAsync(await fx.Executor(session), "WriteFile", "{\"path\":\"same.txt\",\"content\":\"same\\n\"}");
        Assert.False(result.IsError, result.Content);
        var rows = await fx.Changes.ListAsync(fx.RootId, null, 10);
        Assert.Empty(rows.Value);
    }

    [Fact]
    public async Task Worktree_child_copies_name_and_branch_and_redacts_secrets()
    {
        await using var fx = await Fixture.CreateAsync();
        var child = await fx.ChildAsync(depth: 1);
        child.WorktreeBranch = "dyson/abcd1234";
        child.WorktreeAbsolutePath = Path.Combine(fx.Root, "worktrees", "abcd1234");

        var result = await CallAsync(await fx.Executor(child), "CreateFile", """{"path":".env.local","content":"TOKEN=secret"}""");
        Assert.False(result.IsError, result.Content);

        var row = Assert.Single((await fx.Changes.ListAsync(fx.RootId, null, 10)).Value);
        Assert.Equal("abcd1234", row.WorktreeName);
        Assert.Equal("dyson/abcd1234", row.BranchName);
        Assert.Equal(DysonWorkspaceToolExecutor.FileChangeRedactedText, row.AddedText);
        Assert.Equal("#1", row.AgentChain);
        Assert.DoesNotContain("TOKEN", row.AddedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertImage_logs_binary_bytes()
    {
        await using var fx = await Fixture.CreateAsync();
        var session = fx.RootSession();
        var png = SmallestPng();
        File.WriteAllBytes(Path.Combine(fx.Root, "in.png"), png);

        var result = await CallAsync(
            await fx.Executor(session),
            "ConvertImage",
            """{"inputFile":"in.png","outputFile":"out.png","desiredFormat":"png","overwrite":true}""");
        Assert.False(result.IsError, result.Content);

        var row = Assert.Single((await fx.Changes.ListAsync(fx.RootId, null, 10)).Value);
        Assert.Equal(DysonFileChangeKind.Binary, row.Kind);
        Assert.Equal("ConvertImage", row.ToolName);
        Assert.Equal("out.png", row.RelativePath);
        Assert.True(row.ByteLength > 0);
        Assert.Null(row.AddedText);
    }

    [Fact]
    public async Task ConvertImage_pdf_logs_one_row_per_page()
    {
        await using var fx = await Fixture.CreateAsync();
        var session = fx.RootSession();
        File.WriteAllBytes(Path.Combine(fx.Root, "doc.pdf"), DysonPdfTests.TinyPdf());

        var result = await CallAsync(
            await fx.Executor(session),
            "ConvertImage",
            """{"inputFile":"doc.pdf","outputFile":"doc.png","desiredFormat":"png","pages":"1-2","dpi":72}""");
        Assert.False(result.IsError, result.Content);

        var rows = (await fx.Changes.ListAsync(fx.RootId, null, 10)).Value;
        Assert.Equal(["doc-p1.png", "doc-p2.png"], rows.Select(r => r.RelativePath).Order());
        Assert.All(rows, r => Assert.Equal(DysonFileChangeKind.Binary, r.Kind));
    }

    private static async Task<DysonFileChangedEvent> WaitForChange(DysonMessageBus bus, Guid rootId)
    {
        var done = new TaskCompletionSource<DysonFileChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = bus.Subscribe<DysonFileChangedEvent>(DysonBusScopes.Session(rootId), e => done.TrySetResult(e));
        Assert.True(sub.IsSuccess, sub.IsError ? sub.Error : null);
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task<DysonToolCallResult> CallAsync(
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

    private static byte[] SmallestPng()
    {
        const string b64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        return Convert.FromBase64String(b64);
    }

    private sealed class FailingChanges : IDysonFileChangeRepository
    {
        public Task<Result<ulong, string>> AppendAsync(DysonFileChangeEntity row, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ulong, string>.AsError("db down"));

        public Task<Result<IReadOnlyList<DysonFileChangeEntity>, string>> ListAsync(
            Guid rootSessionId, ulong? beforeId, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<DysonFileChangeEntity?, string>> GetAsync(ulong id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession : DysonAgentSession
    {
        public StubSession(string mode, DysonAgentSessionConfig config)
            : base(mode, config, new StubProvider())
        {
        }

        public void Persist(Guid id) => SetPersistenceId(id);

        public void Link(DysonAgentSession child) => RegisterSubagent(child);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode, string task, string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null, string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(string prompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(string prompt, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(DysonAgentTurn turn, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(string planRelativePath, IReadOnlyList<string>? reportBlocks = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(DysonAgentInterrupt interrupt, string? title = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(string instruction, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(DysonAgentInterrupt interrupt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _dbPath;
        private readonly DysonSessionRepository _sessions;

        private Fixture(
            string dbPath,
            string root,
            Guid rootId,
            Guid workDirectoryId,
            DysonFileChangeRepository changes,
            DysonSessionSecretRepository secrets,
            DysonMessageBus bus,
            DysonSessionRepository sessions)
        {
            _dbPath = dbPath;
            _sessions = sessions;
            Root = root;
            RootId = rootId;
            WorkDirectoryId = workDirectoryId;
            Changes = changes;
            Secrets = secrets;
            Bus = bus;
            Config = new DysonAgentSessionConfig { FileChanges = changes, Bus = bus };
        }

        public string Root { get; }
        public Guid RootId { get; }
        public Guid WorkDirectoryId { get; }
        public DysonFileChangeRepository Changes { get; }
        public DysonSessionSecretRepository Secrets { get; }
        public DysonMessageBus Bus { get; }
        public DysonAgentSessionConfig Config { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var (accessor, path) = DysonTempDb.OpenFileAccessor();
            var sessions = DysonTempDb.Sessions(accessor);
            var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                AgentMode = DysonAgentModes.MetaAgent,
                SystemPromptSnapshot = "root",
                Title = "change-stream",
            });
            if (created.IsError)
                throw new InvalidOperationException(created.Error);

            var root = Path.Combine(Path.GetTempPath(), "dyson-fc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(
                path,
                root,
                created.Value,
                Guid.NewGuid(),
                DysonTempDb.FileChanges(accessor),
                DysonTempDb.SessionSecrets(accessor),
                new DysonMessageBus(),
                sessions);
        }

        public StubSession RootSession(bool persisted = true)
        {
            var session = new StubSession(DysonAgentModes.MetaAgent, Config);
            if (persisted)
                session.Persist(RootId);
            Grant(session);
            return session;
        }

        /// <summary>A chain of <paramref name="depth"/> children under the root. Depth 2 yields chain #1-&gt;#1 (ids are per parent).</summary>
        public async Task<StubSession> ChildAsync(int depth)
        {
            DysonAgentSession parent = RootSession();
            StubSession child = null!;
            for (var i = 0; i < depth; i++)
            {
                // Real sessions row: file_changes.SessionId is an FK to sessions.
                var created = await _sessions.CreateSessionAsync(new DysonSessionCreateRequest
                {
                    AgentMode = DysonAgentModes.MetaAgentDrone,
                    SystemPromptSnapshot = "child",
                    Title = "child",
                });
                if (created.IsError)
                    throw new InvalidOperationException(created.Error);

                child = new StubSession(DysonAgentModes.MetaAgentDrone, Config);
                child.Persist(created.Value);
                ((StubSession)parent).Link(child);
                Grant(child);
                parent = child;
            }

            return child;
        }

        public Task<DysonWorkspaceToolExecutor> Executor(DysonAgentSession session) =>
            DysonWorkspaceTestFs.CreateExecutorAsync(
                session, Root, new HttpClient(), workDirectoryId: WorkDirectoryId);

        public async ValueTask DisposeAsync()
        {
            Bus.Dispose();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
                if (File.Exists(_dbPath))
                    File.Delete(_dbPath);
            }
            catch (IOException)
            {
            }

            await Task.CompletedTask;
        }

        private static void Grant(DysonAgentSession session)
        {
            var meta = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent);
            var work = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.Work);
            // CreateFile / WriteFile / ConvertImage are on the Work catalog; WriteSecureDataToFile is on Meta.
            foreach (var name in new[] { "CreateFile", "WriteFile", "ConvertImage" })
                session.McpPipeline.Tools[name] = work.Tools[name];
            session.McpPipeline.Tools["WriteSecureDataToFile"] = meta.Tools["WriteSecureDataToFile"];
        }
    }
}

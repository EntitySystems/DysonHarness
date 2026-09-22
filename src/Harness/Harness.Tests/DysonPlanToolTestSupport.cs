using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>Shared in-memory plans DB + stub session for meta plan-tool executor tests.</summary>
internal sealed class DysonPlanToolFixture : IAsyncDisposable
{
    private DysonPlanToolFixture(
        SqliteConnection connection,
        DysonPlanRepository plans,
        Guid workDirectoryId,
        Guid otherWorkDirectoryId)
    {
        Connection = connection;
        Plans = plans;
        WorkDirectoryId = workDirectoryId;
        OtherWorkDirectoryId = otherWorkDirectoryId;
    }

    public SqliteConnection Connection { get; }
    public DysonPlanRepository Plans { get; }
    public Guid WorkDirectoryId { get; }
    public Guid OtherWorkDirectoryId { get; }

    public static async Task<DysonPlanToolFixture> CreateAsync()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        var subject = DysonTempDb.Subject();
        var plans = DysonTempDb.Plans(accessor, subject);
        var wd = await SeedWorkDirectoryAsync(accessor, subject.SubjectId);
        var other = await SeedWorkDirectoryAsync(accessor, subject.SubjectId);
        return new DysonPlanToolFixture(connection, plans, wd, other);
    }

    public Task<DysonWorkspaceToolExecutor> ExecutorAsync(
        DysonAgentSession session,
        HttpClient http,
        Guid? workDirectoryId = null) =>
        DysonWorkspaceTestFs.CreateExecutorAsync(
            session,
            Path.GetTempPath(),
            http,
            workDirectoryId: workDirectoryId ?? WorkDirectoryId,
            plans: Plans);

    public async Task<long> CreatePlanAsync(
        string title = "A plan",
        string markdown = "# body",
        Guid? workDirectoryId = null,
        DysonPlanStatus status = DysonPlanStatus.Draft,
        Guid? buildAgentId = null)
    {
        var created = await Plans.CreateAsync(
            workDirectoryId ?? WorkDirectoryId,
            DysonPlanKind.MetaPlan,
            title,
            markdown,
            planRelativePath: null,
            status,
            buildAgentId: buildAgentId);
        if (created.IsError)
            throw new InvalidOperationException(created.Error);
        return created.Value;
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    private static async Task<Guid> SeedWorkDirectoryAsync(DysonDbAccessor accessor, string subjectId)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await accessor.RunAsync(async (db, ct) =>
        {
            db.WorkDirectories.Add(new DysonWorkDirectoryEntity
            {
                Id = id,
                SubjectId = subjectId,
                Name = "plan-tools-test",
                AbsolutePath = Path.Combine(Path.GetTempPath(), id.ToString("N")),
                CreatedUtc = now,
                LastOpenedUtc = now,
            });
            await DysonDbAccessor.SaveChangesAsync(db, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }
}

internal sealed class DysonPlanToolStubProvider : DysonAgentProvider;

internal sealed class DysonPlanToolStubSession : DysonAgentSession
{
    public DysonPlanToolStubSession(string mode)
        : base(mode, new DysonAgentSessionConfig(), new DysonPlanToolStubProvider())
    {
    }

    public string? LastSpawnedTask { get; private set; }

    public DysonPlanToolStubSession? LastSpawned { get; private set; }

    public DysonAgentTurn? LastPromptTurn { get; private set; }

    public List<string> PromptInstructions { get; } = [];

    public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

    public void SetPersistenceIdForTest(Guid persistenceId) => SetPersistenceId(persistenceId);

    public static void GrantTool(DysonAgentSession session, string toolName, string catalogMode)
    {
        var pipeline = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), catalogMode);
        session.McpPipeline.Tools[toolName] = pipeline.Tools[toolName];
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
    {
        var child = new DysonPlanToolStubSession(agentMode);
        child.SetPersistenceId(Guid.NewGuid());
        RegisterSubagent(child);
        LastSpawned = child;
        LastSpawnedTask = task;
        return Task.FromResult(Result<DysonStartSubagentResult, string>.AsValue(new DysonStartSubagentResult
        {
            SubagentId = child.Id,
            PersistenceId = child.PersistenceId,
            AgentMode = agentMode,
            Title = "plan-tool-drone",
        }));
    }

    public override Task<VoidResult<string>> LoadFunctionalContextAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptAsync(
        string prompt,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptAsync(
        string prompt,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptHarnessTurnAsync(
        DysonAgentTurn turn,
        CancellationToken cancellationToken = default)
    {
        LastPromptTurn = turn;
        if (!string.IsNullOrWhiteSpace(turn.Instruction))
            PromptInstructions.Add(turn.Instruction);
        return Task.FromResult(VoidResult<string>.Success);
    }

    public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
        string planRelativePath,
        IReadOnlyList<string>? reportBlocks = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
        DysonAgentInterrupt interrupt,
        string? title = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
        string instruction,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptShellExitedAsync(
        DysonAgentInterrupt interrupt,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result<DysonAgentSessionEvent, string>.AsError("not used"));
}

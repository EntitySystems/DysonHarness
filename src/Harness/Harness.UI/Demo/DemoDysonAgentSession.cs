using DysonHarness;

namespace Harness.UI.Demo;

public sealed class DemoDysonAgentSession : DysonAgentSession
{
    public DemoDysonAgentSession(
        string agentMode,
        DysonAgentSessionConfig config,
        DysonAgentProvider provider)
        : base(agentMode, config, provider)
    {
    }

    /// <summary>
    /// Creates a new persisted root session and assigns <see cref="DysonAgentSession.PersistenceId"/>.
    /// </summary>
    public static async Task<Result<DemoDysonAgentSession, string>> CreateAsync(
        DysonSessionStore store,
        DemoDysonAgentProvider provider,
        string agentMode = DysonAgentModes.Work,
        DysonAgentSessionConfig? config = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);

        config ??= new DysonAgentSessionConfig();
        var session = new DemoDysonAgentSession(agentMode, config, provider);

        var create = await store.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = agentMode,
                ModelSlugId = provider.SlugId,
                McpAccessMode = config.McpAccessMode,
                Title = title ?? "New session",
                SystemPromptSnapshot = session.SystemPrompt,
                Status = DysonSessionStatus.Active,
            },
            cancellationToken).ConfigureAwait(false);

        if (create.IsError)
            return Result<DemoDysonAgentSession, string>.AsError(create.Error);

        session.SetPersistenceId(create.Value);

        var createdLog = DysonSessionLogPayload.CreateEntry(
            create.Value,
            DysonSessionLogKind.SessionCreated,
            new DysonSessionLogSessionCreated(create.Value, agentMode, RuntimeId: 0));

        var append = await store.AppendLogAsync(createdLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<DemoDysonAgentSession, string>.AsError(append.Error);

        return Result<DemoDysonAgentSession, string>.AsValue(session);
    }

    /// <summary>
    /// Loads a persisted session, hydrates turns/logs, and appends a SessionResumed log.
    /// </summary>
    public static async Task<Result<DemoDysonAgentSession, string>> LoadAsync(
        DysonSessionStore store,
        Guid sessionId,
        DemoDysonAgentProvider provider,
        DysonAgentSessionConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);

        var full = await store.GetFullSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (full.IsError)
            return Result<DemoDysonAgentSession, string>.AsError(full.Error);

        var state = full.Value;
        config ??= new DysonAgentSessionConfig
        {
            McpAccessMode = state.Session.McpAccessMode,
        };

        var session = new DemoDysonAgentSession(state.Session.AgentMode, config, provider);
        session.RestoreFromPersisted(state);

        var resumedLog = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.SessionResumed,
            new DysonSessionLogSessionResumed(sessionId));

        var append = await store.AppendLogAsync(resumedLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<DemoDysonAgentSession, string>.AsError(append.Error);

        return Result<DemoDysonAgentSession, string>.AsValue(session);
    }

    public override Task<VoidResult<string>> LoadFunctionalContextAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptAsync(
        string prompt,
        CancellationToken cancellationToken = default) =>
        PromptAsync(prompt, [], cancellationToken);

    public override async Task<VoidResult<string>> PromptAsync(
        string prompt,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(filePaths);

        OptimizeContextIfNeeded();

        AppendLog($"prompt: {Truncate(prompt, 120)}");

        var turn = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "",
            ToolName = "read_file",
            Stage = 0,
            ArgumentsJson = """{"path":"README.md"}""",
        });
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "",
            ToolName = "grep",
            Stage = 0,
            ArgumentsJson = """{"pattern":"Dyson","path":"."}""",
        });
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "",
            ToolName = "list_dir",
            Stage = 1,
            ArgumentsJson = """{"path":"src"}""",
        });

        AddTurn(turn);

        var staged = await DysonToolCallScheduler.RunStagedAsync(
            turn,
            ExecuteMockToolAsync,
            cancellationToken).ConfigureAwait(false);

        if (staged.IsError)
            return staged;

        var reply =
            $"# Demo turn\n\nProcessed: {Truncate(prompt, 200)}\n\n" +
            $"Tools completed: {turn.TrackedToolCalls.Count} " +
            $"(provider: {DescribeProvider()}).";

        var parsed = DysonAgentTurn.TryParseAgentTitle(reply);
        if (parsed.IsSuccess)
        {
            turn.AgentTitle = parsed.Value.Title;
            turn.AssistantText = parsed.Value.Body;
        }
        else
        {
            turn.AssistantText = reply;
        }

        AppendLog($"turn complete: {turn.AgentTitle ?? turn.Id.ToString("N")[..8]}");
        return VoidResult<string>.Success;
    }

    public override async Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
        CancellationToken cancellationToken = default)
    {
        var interrupt = await WaitForInterruptAsync(cancellationToken).ConfigureAwait(false);
        if (interrupt.IsError)
            return Result<DysonAgentSessionEvent, string>.AsError(interrupt.Error);

        return Result<DysonAgentSessionEvent, string>.AsValue(
            new DysonSubagentInterruptEvent
            {
                Interrupt = interrupt.Value,
            });
    }

    private string DescribeProvider() =>
        Provider is DemoDysonAgentProvider demo
            ? $"{demo.ProviderKind}/{demo.Slug}"
            : Provider.GetType().Name;

    private static async Task<DysonToolCallResult> ExecuteMockToolAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var delayMs = 180 + (Math.Abs(call.ToolName.GetHashCode(StringComparison.Ordinal)) % 220);
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

        return new DysonToolCallResult
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = $"[demo] {call.ToolName} ok — args={Truncate(call.ArgumentsJson, 80)}",
        };
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value[..max] + "…";
    }
}

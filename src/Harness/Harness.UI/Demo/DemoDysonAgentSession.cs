using System.Text.Json;
using DysonHarness;

namespace Harness.UI.Demo;

public sealed class DemoDysonAgentSession : DysonAgentSession
{
    private readonly DysonSessionStore? _store;

    public DemoDysonAgentSession(
        string agentMode,
        DysonAgentSessionConfig config,
        DysonAgentProvider provider,
        DysonSessionStore? store = null)
        : base(agentMode, config, provider)
    {
        _store = store;
    }

    /// <summary>
    /// Creates a new persisted root session and assigns <see cref="DysonAgentSession.PersistenceId"/>.
    /// </summary>
    public static async Task<Result<DemoDysonAgentSession, string>> CreateAsync(
        DysonSessionStore store,
        DemoDysonAgentProvider provider,
        Guid workDirectoryId,
        string agentMode = DysonAgentModes.Work,
        DysonAgentSessionConfig? config = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);

        if (workDirectoryId == Guid.Empty)
            return Result<DemoDysonAgentSession, string>.AsError("Work directory is required.");

        config ??= new DysonAgentSessionConfig();
        var session = new DemoDysonAgentSession(agentMode, config, provider, store);
        var initialTitle = title ?? "New session";
        session.SetDisplayTitle(initialTitle);

        var create = await store.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = agentMode,
                ModelSlugId = provider.SlugId,
                WorkDirectoryId = workDirectoryId,
                McpAccessMode = config.McpAccessMode,
                Title = initialTitle,
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

        var session = new DemoDysonAgentSession(state.Session.AgentMode, config, provider, store);
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

        var isRenameReview = TurnHistory.Count % DysonSessionInitialization.RenameSessionReviewInterval == 0;
        var turn = TurnHistory.Count == 0
            ? DysonSessionInitialization.CreateTurn(prompt)
            : new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = prompt,
            };

        // Mock RenameSession only on review cadence (turns 1, 9, 17, …) — not every turn.
        if (isRenameReview)
        {
            var renameTitle = Truncate(prompt.Trim(), 64);
            turn.ToolCalls.Add(new DysonToolCall
            {
                CallId = "",
                ToolName = "RenameSession",
                Stage = 0,
                ArgumentsJson = JsonSerializer.Serialize(new { title = renameTitle }),
            });
        }

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

        try
        {
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

            foreach (var chunk in ChunkForStreaming(reply, chunkSize: 12))
            {
                turn.AppendStreamingDelta(chunk);
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            // Title parse only at finalize — preview stays raw until FinishStreaming.
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

            turn.FinishStreaming();

            AppendLog($"turn complete: {turn.AgentTitle ?? turn.Id.ToString("N")[..8]}");
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn.ClearStreamingPreview();
            return new VoidResult<string>("Prompt was cancelled.");
        }
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

    private async Task<DysonToolCallResult> ExecuteMockToolAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (string.Equals(call.ToolName, "RenameSession", StringComparison.OrdinalIgnoreCase))
            return await ExecuteRenameSessionAsync(call, cancellationToken).ConfigureAwait(false);

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

    private async Task<DysonToolCallResult> ExecuteRenameSessionAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string? title = null;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("title", out var titleProp))
                title = titleProp.GetString();
        }
        catch (JsonException)
        {
            return new DysonToolCallResult
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Stage = call.Stage,
                IsError = true,
                Content = "RenameSession: invalid JSON arguments.",
            };
        }

        var rename = await RenameAsync(title ?? "", cancellationToken).ConfigureAwait(false);
        if (rename.IsError)
        {
            return new DysonToolCallResult
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Stage = call.Stage,
                IsError = true,
                Content = rename.Error,
            };
        }

        if (_store is not null && PersistenceId != Guid.Empty)
        {
            var persist = await _store.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = PersistenceId,
                    Title = DisplayTitle,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
            {
                return new DysonToolCallResult
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    Stage = call.Stage,
                    IsError = true,
                    Content = persist.Error,
                };
            }

            var renamedLog = DysonSessionLogPayload.CreateEntry(
                PersistenceId,
                DysonSessionLogKind.SessionRenamed,
                new DysonSessionLogSessionRenamed(DisplayTitle!));

            await _store.AppendLogAsync(renamedLog, cancellationToken).ConfigureAwait(false);
        }

        return new DysonToolCallResult
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = $"Renamed session to \"{DisplayTitle}\".",
        };
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value[..max] + "…";
    }

    private static IEnumerable<string> ChunkForStreaming(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, text.Length - i);
            yield return text.Substring(i, len);
        }
    }
}

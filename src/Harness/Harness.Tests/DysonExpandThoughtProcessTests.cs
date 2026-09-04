using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only ExpandThoughtProcess MCP enqueue / EndsCurrentTurn / DropTurnContext /
/// RestoreTurnContext / turnId transcript headers / continuation gate (Xunit Fact).
/// </summary>
public class DysonExpandThoughtProcessTests
{
    [Fact]
    public async Task Run()
    {
        AssertInstructionAndContinuation();
        await AssertExpandEnqueuesAndEndsTurn();
        await AssertExpandRecursionBlocked();
        await AssertDropAndRestoreTurnContext();
        AssertTranscriptTurnIdAndOmitExcluded();
        AssertSoftCloseEndsCurrentTurn();
        AssertSoftClosePreservesModelContent();
        AssertSoftCloseToolSpecificNotes();
    }

    private static void AssertInstructionAndContinuation()
    {
        if (!DysonExpandThoughtProcess.Instruction.Contains("DropTurnContext", StringComparison.Ordinal)
            || !DysonExpandThoughtProcess.Instruction.Contains("SummarizeTurns", StringComparison.Ordinal)
            || !DysonExpandThoughtProcess.Instruction.Contains("turn id", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ExpandThoughtProcess Instruction must mention SummarizeTurns / DropTurnContext and turn ids.");
        }

        var turn = DysonExpandThoughtProcess.CreateTurn("clarify auth");
        if (turn.Kind != DysonAgentTurnKind.ExpandThoughtProcess
            || turn.Instruction is null
            || !turn.Instruction.Contains("Focus: clarify auth", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CreateTurn must set ExpandThoughtProcess + Focus appendix.");
        }

        if (!DysonExpandThoughtProcess.ShouldEnqueueContinuation(DysonAgentTurnKind.ExpandThoughtProcess)
            || DysonExpandThoughtProcess.ShouldEnqueueContinuation(DysonAgentTurnKind.Normal)
            || DysonExpandThoughtProcess.ShouldEnqueueContinuation(DysonAgentTurnKind.BeginBuildPlan))
        {
            throw new InvalidOperationException(
                "ShouldEnqueueContinuation must be true only for ExpandThoughtProcess.");
        }

        if (string.IsNullOrWhiteSpace(DysonExpandThoughtProcess.ContinuationPrompt))
            throw new InvalidOperationException("ContinuationPrompt must be non-empty.");

        var preamble = DysonAgentSystemPrompts.SharedPreamble;
        if (preamble.IndexOf("ends the current turn", StringComparison.OrdinalIgnoreCase) < 0
            || preamble.IndexOf("DropTurnContext", StringComparison.Ordinal) < 0
            || preamble.IndexOf("SummarizeTurns", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "SharedPreamble must note ExpandThoughtProcess ends the turn, SummarizeTurns, and DropTurnContext.");
        }
    }

    private static async Task AssertExpandEnqueuesAndEndsTurn()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);

        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "do work",
            StartedUtc = DateTime.UtcNow,
        });

        var result = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "e1",
            ToolName = "ExpandThoughtProcess",
            Stage = 0,
            ArgumentsJson = """{"focus":"noise"}""",
        }).GetAwaiter().GetResult();

        if (result.IsError)
            throw new InvalidOperationException("ExpandThoughtProcess should succeed: " + result.Content);

        if (!result.EndsCurrentTurn)
            throw new InvalidOperationException("ExpandThoughtProcess must set EndsCurrentTurn.");

        if (!session.TryDequeuePendingTurn(out var pending)
            || pending.Kind != DysonAgentTurnKind.ExpandThoughtProcess
            || pending.Instruction is null
            || !pending.Instruction.Contains("Focus: noise", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ExpandThoughtProcess must enqueue ExpandThoughtProcess with focus.");
        }

        if (!result.Content.Contains("ExpandThoughtProcess", StringComparison.Ordinal))
            throw new InvalidOperationException("Success JSON should note nextTurnKind.");
    }

    private static async Task AssertExpandRecursionBlocked()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);

        session.AddTurnForTest(DysonExpandThoughtProcess.CreateTurn());
        if (!session.IsInExpandThoughtProcessPhase)
            throw new InvalidOperationException("Expected IsInExpandThoughtProcessPhase.");

        var blocked = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "e2",
            ToolName = "ExpandThoughtProcess",
            Stage = 0,
            ArgumentsJson = "{}",
        }).GetAwaiter().GetResult();

        if (!blocked.IsError
            || blocked.Content.IndexOf("recursion", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Nested ExpandThoughtProcess must fail: " + blocked.Content);
        }
    }

    private static async Task AssertDropAndRestoreTurnContext()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, Path.GetTempPath(), http);

        var noisy = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "rabbit hole",
            AssistantText = "dead end",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(noisy);

        var current = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "continue",
            StartedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(current);

        var missingReason = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "d0",
            ToolName = "DropTurnContext",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{noisy.Id}}"]}""",
        }).GetAwaiter().GetResult();
        if (!missingReason.IsError
            || missingReason.Content.IndexOf("reason", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "DropTurnContext must require reason: " + missingReason.Content);
        }

        var refuseSelf = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "d1",
            ToolName = "DropTurnContext",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{current.Id}}"],"reason":"self"}""",
        }).GetAwaiter().GetResult();
        if (refuseSelf.IsError)
            throw new InvalidOperationException("Self-drop should soft-skip, not hard-fail: " + refuseSelf.Content);
        if (current.IsExcludedFromContext)
            throw new InvalidOperationException("In-flight turn must not be excluded.");
        if (refuseSelf.Content.IndexOf("in-flight", StringComparison.OrdinalIgnoreCase) < 0
            && refuseSelf.Content.IndexOf("skipped", StringComparison.OrdinalIgnoreCase) < 0
            && refuseSelf.Content.IndexOf("partial", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Self-drop should report skipped/partial: " + refuseSelf.Content);
        }

        var dropReason = "obsolete rabbit hole";
        var drop = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "d2",
            ToolName = "DropTurnContext",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{noisy.Id}}","{{Guid.NewGuid()}}"],"reason":"{{dropReason}}"}""",
        }).GetAwaiter().GetResult();
        if (drop.IsError)
            throw new InvalidOperationException("DropTurnContext should succeed on Normal turn: " + drop.Content);
        if (!noisy.IsExcludedFromContext)
            throw new InvalidOperationException("DropTurnContext must set IsExcludedFromContext.");
        if (drop.Content.IndexOf("partial", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Unknown id should yield partial status: " + drop.Content);
        if (drop.Content.IndexOf(dropReason, StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Success JSON must include reason: " + drop.Content);

        var dropLog = $"Turn {noisy.Id:D} dropped, reason: {dropReason}";
        if (!session.SnapshotLog().Any(l => l.Equals(dropLog, StringComparison.Ordinal)))
            throw new InvalidOperationException("Drop must AppendLog: " + dropLog);

        var restoreMissingReason = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r0",
            ToolName = "RestoreTurnContext",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{noisy.Id}}"]}""",
        }).GetAwaiter().GetResult();
        if (!restoreMissingReason.IsError
            || restoreMissingReason.Content.IndexOf("reason", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "RestoreTurnContext must require reason: " + restoreMissingReason.Content);
        }

        var restoreReason = "still needed";
        var restore = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r1",
            ToolName = "RestoreTurnContext",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{noisy.Id}}","{{Guid.NewGuid()}}"],"reason":"{{restoreReason}}"}""",
        }).GetAwaiter().GetResult();
        if (restore.IsError)
            throw new InvalidOperationException("RestoreTurnContext should succeed: " + restore.Content);
        if (noisy.IsExcludedFromContext)
            throw new InvalidOperationException("RestoreTurnContext must clear IsExcludedFromContext.");
        if (restore.Content.IndexOf("partial", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Unknown id should yield partial restore: " + restore.Content);

        var restoreLog = $"Turn {noisy.Id:D} restored, reason: {restoreReason}";
        if (!session.SnapshotLog().Any(l => l.Equals(restoreLog, StringComparison.Ordinal)))
            throw new InvalidOperationException("Restore must AppendLog: " + restoreLog);

        var already = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "r2",
            ToolName = "RestoreTurnContext",
            Stage = 0,
            ArgumentsJson = $$"""{"turnIds":["{{noisy.Id}}"],"reason":"again"}""",
        }).GetAwaiter().GetResult();
        if (already.IsError)
            throw new InvalidOperationException("Idempotent restore should succeed: " + already.Content);
        var againLog = $"Turn {noisy.Id:D} restored, reason: again";
        if (session.SnapshotLog().Count(l => l.Equals(againLog, StringComparison.Ordinal)) > 0)
            throw new InvalidOperationException("Idempotent restore must not re-log.");
    }

    private static void AssertTranscriptTurnIdAndOmitExcluded()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var kept = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "keep me",
            AssistantText = "kept reply",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        var dropped = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "drop me",
            AssistantText = "dropped reply",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            IsExcludedFromContext = true,
        };
        session.AddTurnForTest(kept);
        session.AddTurnForTest(dropped);

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        var json = completions.Messages.ToJsonString();
        var header = $"[turnId={kept.Id:D}]";
        if (!json.Contains(header, StringComparison.Ordinal)
            || !json.Contains("keep me", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Completions history must include [turnId=…] header for kept turns.");
        }

        if (json.Contains($"[turnId={dropped.Id:D}]", StringComparison.Ordinal)
            || json.Contains("drop me", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Completions history must omit excluded turns entirely.");
        }

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        var responsesJson = responses.Input.ToJsonString();
        if (!responsesJson.Contains(header, StringComparison.Ordinal)
            || responsesJson.Contains("drop me", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Responses history must include turnId for kept turns and omit excluded.");
        }
    }

    private static void AssertSoftCloseEndsCurrentTurn()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "working",
            StartedUtc = DateTime.UtcNow,
        };
        turn.AppendStreamingDelta("partial");

        var result = OpenAiCompatibleAgentSession.SoftCloseAfterEndsCurrentTurn(
            turn,
            endingToolName: "ExpandThoughtProcess");
        if (result.IsError)
            throw new InvalidOperationException("SoftCloseAfterEndsCurrentTurn must succeed.");
        if (string.IsNullOrWhiteSpace(turn.AssistantText)
            || turn.AgentTitle is null
            || turn.IsStreaming)
        {
            throw new InvalidOperationException(
                "SoftCloseAfterEndsCurrentTurn must finalize assistant text and stop streaming.");
        }

        if (turn.AgentTitle.IndexOf("Expanding", StringComparison.OrdinalIgnoreCase) < 0
            || turn.AssistantText.IndexOf("ExpandThoughtProcess", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "ExpandThoughtProcess soft-close should use ExpandThoughtProcess harness note.");
        }
    }

    private static void AssertSoftClosePreservesModelContent()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "working",
            StartedUtc = DateTime.UtcNow,
        };
        turn.AppendStreamingDelta("will be cleared");

        const string modelText = "# First paragraph\n\nKeep this fifty-word reply intact.";
        var result = OpenAiCompatibleAgentSession.SoftCloseAfterEndsCurrentTurn(
            turn,
            endingToolName: "StartNewTurn",
            modelContent: modelText);
        if (result.IsError)
            throw new InvalidOperationException("SoftClose with model content must succeed.");
        if (turn.AgentTitle != "First paragraph"
            || turn.AssistantText is null
            || !turn.AssistantText.Contains("Keep this fifty-word", StringComparison.Ordinal)
            || turn.IsStreaming)
        {
            throw new InvalidOperationException(
                "SoftClose must preserve non-empty model content over harness notes.");
        }
    }

    private static void AssertSoftCloseToolSpecificNotes()
    {
        var startTurn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "working",
            StartedUtc = DateTime.UtcNow,
        };
        OpenAiCompatibleAgentSession.SoftCloseAfterEndsCurrentTurn(startTurn, "StartNewTurn");
        if (startTurn.AgentTitle is null
            || startTurn.AgentTitle.IndexOf("Starting", StringComparison.OrdinalIgnoreCase) < 0
            || startTurn.AssistantText is null
            || startTurn.AssistantText.IndexOf("StartNewTurn", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "StartNewTurn soft-close without model content must use StartNewTurn note.");
        }

        var generic = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "working",
            StartedUtc = DateTime.UtcNow,
        };
        OpenAiCompatibleAgentSession.SoftCloseAfterEndsCurrentTurn(generic, "SomeOtherEndTool");
        if (generic.AgentTitle is null
            || generic.AgentTitle.IndexOf("Turn ended", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Unknown end-turn tool must use generic soft-close note.");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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
            => Task.FromResult(VoidResult<string>.Success);

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
            => throw new NotSupportedException();
    }
}

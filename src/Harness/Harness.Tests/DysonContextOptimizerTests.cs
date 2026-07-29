using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: compact tool-line format, transcript role/prefix for optimized history, echo strip.
/// </summary>
public class DysonContextOptimizerTests
{
    [Fact]
    public void Run()
    {
        AssertFormatCompactToolLineUsesTaggedShape();
        AssertIsOnlyCompactToolHistoryEcho();
        AssertResolveFinalAssistantContentStripsEcho();
        AssertOptimizedTranscriptEmitsUserRoleWithHarnessPrefix();
    }

    private static void AssertFormatCompactToolLineUsesTaggedShape()
    {
        var call = new DysonToolCall
        {
            CallId = "c1",
            ToolName = "ShellExecute",
            Stage = 0,
            ArgumentsJson = """{"command":"echo hi","shell":"PowerShell"}""",
        };
        var result = new DysonToolCallResult
        {
            CallId = "c1",
            ToolName = "ShellExecute",
            Stage = 0,
            Content = "exitCode=0\nok",
        };

        var line = DysonContextOptimizer.FormatCompactToolLine(call, result);
        if (!line.StartsWith("[compact] ShellExecute params:", StringComparison.Ordinal)
            || !line.Contains("|| result:", StringComparison.Ordinal)
            || line.StartsWith("Called ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected tagged [compact] line, got: {line}");
        }

        var turns = new List<DysonAgentTurn>
        {
            MakeCompletedToolTurn(call, result),
            MakeCompletedToolTurn(
                new DysonToolCall
                {
                    CallId = "c2",
                    ToolName = "Grep",
                    Stage = 0,
                    ArgumentsJson = """{"pattern":"x"}""",
                },
                new DysonToolCallResult
                {
                    CallId = "c2",
                    ToolName = "Grep",
                    Stage = 0,
                    Content = "match",
                }),
            new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = "keep recent",
                AssistantText = "ok",
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
            },
        };

        var optimizer = new DysonContextOptimizer { KeepRecentTurns = 1, MaxTurnsBeforeOptimize = 1 };
        var opt = optimizer.Optimize(turns, new CharTokenCounter());
        if (opt.IsError)
            throw new InvalidOperationException(opt.Error);

        var compactHistory = turns[0].CompactToolHistory;
        if (!turns[0].ToolHistoryOptimized
            || string.IsNullOrEmpty(compactHistory)
            || !compactHistory.Contains("[compact] ShellExecute", StringComparison.Ordinal)
            || compactHistory.Contains("Called ShellExecute", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"BuildCompactHistory must use [compact] tags, got: {compactHistory}");
        }
    }

    private static void AssertIsOnlyCompactToolHistoryEcho()
    {
        var compact =
            "[compact] ShellExecute params: command=(7 chars), shell=PowerShell || result: exitCode=0 (10 chars)";
        if (!DysonContextOptimizer.IsOnlyCompactToolHistoryEcho(compact))
            throw new InvalidOperationException("New [compact] line must count as echo.");

        var legacy =
            "Called ShellExecute with params: command=(167 chars), shell=PowerShell || result: exitCode=0 (1147 chars)";
        if (!DysonContextOptimizer.IsOnlyCompactToolHistoryEcho(legacy))
            throw new InvalidOperationException("Legacy Called… line must count as echo.");

        var multi = compact + "\n" + legacy;
        if (!DysonContextOptimizer.IsOnlyCompactToolHistoryEcho(multi))
            throw new InvalidOperationException("Multi-line compact-only body must count as echo.");

        if (DysonContextOptimizer.IsOnlyCompactToolHistoryEcho(
                "# Done\n\n" + compact))
        {
            throw new InvalidOperationException("Mixed title + compact must not count as echo.");
        }

        if (DysonContextOptimizer.IsOnlyCompactToolHistoryEcho("real assistant prose"))
            throw new InvalidOperationException("Normal prose must not count as echo.");

        if (DysonContextOptimizer.IsOnlyCompactToolHistoryEcho(null)
            || DysonContextOptimizer.IsOnlyCompactToolHistoryEcho("   "))
        {
            throw new InvalidOperationException("Blank content is not an echo.");
        }
    }

    private static void AssertResolveFinalAssistantContentStripsEcho()
    {
        var echo =
            "[compact] ShellExecute params: command=(7 chars) || result: exitCode=0 (2 chars)";
        if (OpenAiCompatibleAgentSession.ResolveFinalAssistantContent(echo) != "")
            throw new InvalidOperationException("Compact echo must resolve to empty assistant body.");

        var legacy =
            "Called ShellExecute with params: command=(167 chars) || result: exitCode=0 (1147 chars)";
        if (OpenAiCompatibleAgentSession.ResolveFinalAssistantContent(legacy) != "")
            throw new InvalidOperationException("Legacy compact echo must resolve to empty.");

        var normal = OpenAiCompatibleAgentSession.ResolveFinalAssistantContent("# Title\n\nBody");
        if (normal != "# Title\n\nBody")
            throw new InvalidOperationException("Normal content must pass through.");

        var empty = OpenAiCompatibleAgentSession.ResolveFinalAssistantContent("  ");
        if (!empty.Contains("Empty reply", StringComparison.Ordinal))
            throw new InvalidOperationException("Blank non-echo must use empty-reply harness note.");
    }

    private static void AssertOptimizedTranscriptEmitsUserRoleWithHarnessPrefix()
    {
        var compact =
            "[compact] ShellExecute params: command=(7 chars), shell=PowerShell || result: exitCode=0 (10 chars)";
        var session = new StubSession();
        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "run shell",
            AssistantText = "done",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            ToolHistoryOptimized = true,
            CompactToolHistory = compact,
        });

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        AssertCompactHistoryAsUser(completions.Messages, compact, "Completions");

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        AssertCompactHistoryAsUser(responses.Input, compact, "Responses");
    }

    private static void AssertCompactHistoryAsUser(JsonArray items, string compact, string label)
    {
        JsonObject? compactMsg = null;
        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;

            var content = obj["content"]?.GetValue<string>();
            if (content is null || !content.Contains(compact, StringComparison.Ordinal))
                continue;

            compactMsg = obj;
            break;
        }

        if (compactMsg is null)
            throw new InvalidOperationException($"{label}: compact history message missing.");

        var role = compactMsg["role"]?.GetValue<string>();
        if (!string.Equals(role, "user", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: compact history must be role=user, got '{role}'.");
        }

        var body = compactMsg["content"]!.GetValue<string>();
        if (!body.StartsWith(
                OpenAiCacheFriendlyTranscriptBuilder.CompactToolHistoryHarnessPrefix,
                StringComparison.Ordinal)
            || !body.Contains(compact, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: compact history must include harness prefix + compact body.");
        }

        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;

            var role2 = obj["role"]?.GetValue<string>();
            var content = obj["content"]?.GetValue<string>();
            if (string.Equals(role2, "assistant", StringComparison.Ordinal)
                && content is not null
                && content == compact)
            {
                throw new InvalidOperationException(
                    $"{label}: must not emit role=assistant with raw CompactToolHistory body.");
            }
        }
    }

    private static DysonAgentTurn MakeCompletedToolTurn(DysonToolCall call, DysonToolCallResult result)
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = $"use {call.ToolName}",
            AssistantText = "ok",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.Add(call);
        turn.ResponseLog.Enqueue(result);
        return turn;
    }

    private sealed class CharTokenCounter : IDysonTokenCounter
    {
        public int CountTokens(string text) => text.Length;
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
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

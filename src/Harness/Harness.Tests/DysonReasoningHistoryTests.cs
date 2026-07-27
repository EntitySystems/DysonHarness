using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: multi-round reasoning log commit, legacy ReasoningText synthesize, transcript omission,
/// thinking expand/collapse helper (Xunit Fact).
/// </summary>
public class DysonReasoningHistoryTests
{
    [Fact]
    public void Run()
    {
        AssertMultiRoundCommitAndReasoningTextJoin();
        AssertPersistRestoreAndLegacySynthesize();
        AssertTranscriptOmitsReasoning();
        AssertExpandCollapseHelper();
    }

    private static void AssertMultiRoundCommitAndReasoningTextJoin()
    {
        var turn = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };

        var round0 = new OpenAiModelReply
        {
            Content = "looking up files…",
            ReasoningContent = "thought A",
        };

        OpenAiCompatibleAgentSession.CommitReasoningRound(turn, round0, roundIndex: 0, isFinalAssistant: true);
        OpenAiCompatibleAgentSession.CommitInterimText(turn, round0.Content, roundIndex: 0);

        var round1ThoughtOnly = new OpenAiModelReply
        {
            Content = "# Done\n\nFinal answer.",
            ReasoningContent = "thought B",
        };
        OpenAiCompatibleAgentSession.CommitReasoningRound(
            turn,
            round1ThoughtOnly,
            roundIndex: 1,
            isFinalAssistant: true);

        if (turn.ReasoningLog.Count != 3)
        {
            throw new InvalidOperationException(
                $"Expected 3 segments (Thought, Interim, Thought), got {turn.ReasoningLog.Count}.");
        }

        if (turn.ReasoningLog[0].Kind != DysonReasoningSegmentKind.Thought
            || turn.ReasoningLog[0].Text != "thought A"
            || turn.ReasoningLog[0].RoundIndex != 0)
        {
            throw new InvalidOperationException("Round 0 Thought segment mismatch.");
        }

        if (turn.ReasoningLog[1].Kind != DysonReasoningSegmentKind.InterimText
            || turn.ReasoningLog[1].Text != "looking up files…")
        {
            throw new InvalidOperationException("Round 0 InterimText segment mismatch.");
        }

        if (turn.ReasoningLog[2].Kind != DysonReasoningSegmentKind.Thought
            || turn.ReasoningLog[2].Text != "thought B"
            || turn.ReasoningLog[2].RoundIndex != 1)
        {
            throw new InvalidOperationException("Round 1 Thought segment mismatch.");
        }

        if (!string.Equals(turn.ReasoningText, "thought A\n\nthought B", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ReasoningText should join Thoughts only, got '{turn.ReasoningText}'.");
        }

        // Final assistant body is separate — not an InterimText.
        if (turn.ReasoningLog.Any(s => s.Text.Contains("Final answer", StringComparison.Ordinal)))
            throw new InvalidOperationException("Final AssistantText must not appear in reasoning log.");
    }

    private static void AssertPersistRestoreAndLegacySynthesize()
    {
        var live = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        live.AppendReasoningRound(0, "alpha", "mid words", includeInterimText: true);
        live.AppendReasoningRound(1, "beta", interimText: null, includeInterimText: false);

        var entity = DysonTurnPersistence.ToEntity(live, Guid.NewGuid(), sequence: 1);
        if (string.IsNullOrWhiteSpace(entity.ReasoningLogJson))
            throw new InvalidOperationException("ToEntity must serialize ReasoningLogJson.");
        if (!string.Equals(entity.ReasoningText, "alpha\n\nbeta", StringComparison.Ordinal))
            throw new InvalidOperationException("ToEntity ReasoningText must be Thought join.");

        var restored = new DysonAgentTurn { Id = entity.Id, Kind = entity.Kind };
        restored.RestoreReasoningLog(
            DysonReasoningLogSerializer.DeserializeOrSynthesize(entity.ReasoningLogJson, entity.ReasoningText));

        if (restored.ReasoningLog.Count != 3
            || restored.ReasoningLog[0].Text != "alpha"
            || restored.ReasoningLog[1].Kind != DysonReasoningSegmentKind.InterimText
            || restored.ReasoningLog[2].Text != "beta"
            || restored.ReasoningText != "alpha\n\nbeta")
        {
            throw new InvalidOperationException("Restore from ReasoningLogJson lost segments.");
        }

        var legacy = DysonReasoningLogSerializer.DeserializeOrSynthesize(
            json: null,
            reasoningText: "legacy blob");
        if (legacy.Count != 1
            || legacy[0].Kind != DysonReasoningSegmentKind.Thought
            || legacy[0].Text != "legacy blob"
            || legacy[0].RoundIndex != 0)
        {
            throw new InvalidOperationException("Legacy ReasoningText must synthesize one Thought.");
        }

        // Log wins over ReasoningText when both present.
        var preferLog = DysonReasoningLogSerializer.DeserializeOrSynthesize(
            entity.ReasoningLogJson,
            reasoningText: "should be ignored");
        if (preferLog.Count != 3 || preferLog[0].Text != "alpha")
            throw new InvalidOperationException("Non-empty log must not be replaced by ReasoningText.");
    }

    private static void AssertTranscriptOmitsReasoning()
    {
        var session = new StubSession();
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "user prompt unique-xyz",
            AssistantText = "assistant body unique-abc",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        turn.AppendReasoningRound(0, "SECRET_THOUGHT_TOKEN", "SECRET_INTERIM_TOKEN", includeInterimText: true);
        turn.AppendReasoningRound(1, "SECRET_THOUGHT_TWO", null, includeInterimText: false);
        session.AddTurnForTest(turn);

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        var completionsJson = completions.Messages.ToJsonString();

        if (!completionsJson.Contains("user prompt unique-xyz", StringComparison.Ordinal)
            || !completionsJson.Contains("assistant body unique-abc", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Transcript must still include instruction + assistant body.");
        }

        foreach (var secret in new[] { "SECRET_THOUGHT_TOKEN", "SECRET_INTERIM_TOKEN", "SECRET_THOUGHT_TWO" })
        {
            if (completionsJson.Contains(secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Completions transcript must omit reasoning; found '{secret}'.");
            }
        }

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        var responsesJson = responses.Input.ToJsonString();
        foreach (var secret in new[] { "SECRET_THOUGHT_TOKEN", "SECRET_INTERIM_TOKEN", "SECRET_THOUGHT_TWO" })
        {
            if (responsesJson.Contains(secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Responses transcript must omit reasoning; found '{secret}'.");
            }
        }
    }

    private static void AssertExpandCollapseHelper()
    {
        // Latest Thought open while no assistant body.
        if (!DysonReasoningHistoryUi.ShouldExpandSegment(1, 2, hasAssistantBody: false, isReasoningStreaming: false))
            throw new InvalidOperationException("Latest Thought should expand before assistant body.");
        if (DysonReasoningHistoryUi.ShouldExpandSegment(0, 2, hasAssistantBody: false, isReasoningStreaming: false))
            throw new InvalidOperationException("Prior Thought should stay collapsed.");

        // All collapsed once assistant body exists (final text) and not streaming reasoning.
        if (DysonReasoningHistoryUi.ShouldExpandSegment(1, 2, hasAssistantBody: true, isReasoningStreaming: false))
            throw new InvalidOperationException("Segments should collapse after AssistantText.");

        // Assistant streaming preview also collapses the log (same gate as final text).
        if (DysonReasoningHistoryUi.ShouldExpandSegment(0, 1, hasAssistantBody: true, isReasoningStreaming: false))
            throw new InvalidOperationException("Segments should collapse while assistant StreamingPreview is shown.");

        // Reasoning streaming keeps latest open even if assistant body already set (handoff edge).
        if (!DysonReasoningHistoryUi.ShouldExpandSegment(0, 1, hasAssistantBody: true, isReasoningStreaming: true))
            throw new InvalidOperationException("Reasoning streaming should keep latest segment open.");

        // InterimText counts as a collapsible slot (Thought + Interim → Interim is latest).
        if (DysonReasoningHistoryUi.ShouldExpandSegment(0, 2, hasAssistantBody: false, isReasoningStreaming: false))
            throw new InvalidOperationException("Prior Thought should collapse when InterimText is the latest slot.");
        if (!DysonReasoningHistoryUi.ShouldExpandSegment(1, 2, hasAssistantBody: false, isReasoningStreaming: false))
            throw new InvalidOperationException("Latest InterimText slot should expand before assistant body.");

        // Live trailing reasoning slot: committed Thought collapses while live is latest.
        if (DysonReasoningHistoryUi.ShouldExpandSegment(0, 2, hasAssistantBody: false, isReasoningStreaming: true))
            throw new InvalidOperationException("Committed Thought should collapse while live reasoning is the latest slot.");
        if (!DysonReasoningHistoryUi.ShouldExpandSegment(1, 2, hasAssistantBody: false, isReasoningStreaming: true))
            throw new InvalidOperationException("Live reasoning slot should expand while streaming.");
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

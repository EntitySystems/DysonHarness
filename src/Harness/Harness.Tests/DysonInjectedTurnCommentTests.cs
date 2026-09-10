using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: in-flight inject-comment queue, session gate, drain formatter, ReasoningLog persist,
/// completed/in-flight/compact/summarized transcript survival (Xunit Fact).
/// </summary>
public class DysonInjectedTurnCommentTests
{
    [Fact]
    public void Run()
    {
        AssertEnqueueValidationAndFlush();
        AssertInjectTurnCommentInFlightGate();
        AssertNestedInFlightStack();
        AssertDrainFormatsAndLeavesReasoningSegments();
        AssertPersistRestoreUserComment();
        AssertCompletedTurnHistoryEmitsComments();
        AssertInFlightDoesNotSpliceCommentsOntoInstruction();
        AssertCompactedTurnKeepsCommentsOnInstruction();
        AssertSummarizedStubKeepsComments();
    }

    private static void AssertEnqueueValidationAndFlush()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "original instruction",
        };

        var fired = 0;
        turn.AssistantTextChanged += (_, _) => fired++;

        ExpectError(turn.EnqueueUserComment(""), "empty comment");
        ExpectError(turn.EnqueueUserComment("   \n\t  "), "whitespace comment");
        ExpectError(turn.EnqueueUserComment(new string('x', 16385)), "over-cap comment");
        if (fired != 0)
            throw new InvalidOperationException("Rejected comments must not flush AssistantTextChanged.");
        if (turn.HasPendingUserComments || turn.ReasoningLog.Count != 0)
            throw new InvalidOperationException("Rejected comments must not enqueue or append segments.");
        if (turn.Instruction != "original instruction")
            throw new InvalidOperationException("Rejected comments must not mutate Instruction.");

        var exactCap = turn.EnqueueUserComment(new string('y', 16384));
        if (exactCap.IsError)
            throw new InvalidOperationException($"Exact 16384-char comment must succeed, got '{exactCap.Error}'.");
        if (fired == 0)
            throw new InvalidOperationException("Successful enqueue must Flush AssistantTextChanged synchronously.");
        if (!turn.HasPendingUserComments)
            throw new InvalidOperationException("Successful enqueue must leave pending comments.");
        if (turn.ReasoningLog.Count != 1
            || turn.ReasoningLog[0].Kind != DysonReasoningSegmentKind.UserComment
            || turn.ReasoningLog[0].Text.Length != 16384
            || turn.ReasoningLog[0].RoundIndex != 0)
        {
            throw new InvalidOperationException("Successful enqueue must append a UserComment segment.");
        }

        if (turn.ReasoningText is not null)
            throw new InvalidOperationException("UserComment must not change ReasoningText.");
        if (turn.Instruction != "original instruction")
            throw new InvalidOperationException("EnqueueUserComment must not mutate Instruction.");
        if (!turn.ResponseLog.IsEmpty || turn.CompactToolHistory is not null)
            throw new InvalidOperationException("Comments must not be stuffed into ResponseLog or CompactToolHistory.");

        turn.AppendReasoningRound(1, "thought after comment", null, includeInterimText: false);
        var secondFired = fired;
        var second = turn.EnqueueUserComment("  trim me  ");
        if (second.IsError)
            throw new InvalidOperationException($"Second comment should succeed, got '{second.Error}'.");
        if (fired <= secondFired)
            throw new InvalidOperationException("Second enqueue must Flush AssistantTextChanged again.");
        if (turn.ReasoningLog[^1].Kind != DysonReasoningSegmentKind.UserComment
            || turn.ReasoningLog[^1].Text != "trim me"
            || turn.ReasoningLog[^1].RoundIndex != 1)
        {
            throw new InvalidOperationException("Comment RoundIndex must copy the last log segment.");
        }

        if (turn.ReasoningText != "thought after comment")
            throw new InvalidOperationException("UserComment must not rewrite ReasoningText.");
    }

    private static void AssertInjectTurnCommentInFlightGate()
    {
        var session = new StubSession();
        var running = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        var completed = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            CompletedUtc = DateTime.UtcNow,
        };
        session.AddTurnForTest(completed);
        session.AddTurnForTest(running);

        ExpectErrorMessage(
            session.InjectTurnComment(running.Id, "too early"),
            "Turn is not currently running.",
            "inject before BeginInFlightPrompt");
        ExpectErrorMessage(
            session.InjectTurnComment(completed.Id, "already done"),
            "Turn is not currently running.",
            "inject completed turn");
        ExpectErrorMessage(
            session.InjectTurnComment(Guid.NewGuid(), "unknown"),
            "Turn is not currently running.",
            "inject unknown id");
        if (session.IsTurnInFlight(running.Id) || session.IsTurnInFlight(completed.Id))
            throw new InvalidOperationException("IsTurnInFlight must be false when the stack is empty.");

        using (session.BeginInFlightPrompt(running))
        {
            if (!session.IsTurnInFlight(running.Id))
                throw new InvalidOperationException("IsTurnInFlight must be true for the stacked turn.");
            if (session.IsTurnInFlight(completed.Id))
                throw new InvalidOperationException("Completed history turns must not count as in-flight.");

            var injected = session.InjectTurnComment(running.Id, "steer now");
            if (injected.IsError)
                throw new InvalidOperationException($"In-flight inject should succeed, got '{injected.Error}'.");
            if (!running.HasPendingUserComments
                || running.ReasoningLog.Count != 1
                || running.ReasoningLog[0].Kind != DysonReasoningSegmentKind.UserComment
                || running.ReasoningLog[0].Text != "steer now")
            {
                throw new InvalidOperationException("In-flight inject must enqueue and append a UserComment segment.");
            }
        }

        ExpectErrorMessage(
            session.InjectTurnComment(running.Id, "after dispose"),
            "Turn is not currently running.",
            "inject after in-flight scope ends");
        if (session.IsTurnInFlight(running.Id))
            throw new InvalidOperationException("IsTurnInFlight must be false after the scope is disposed.");
    }

    private static void AssertNestedInFlightStack()
    {
        var session = new StubSession();
        var outer = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        var inner = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        session.AddTurnForTest(outer);
        session.AddTurnForTest(inner);

        using (session.BeginInFlightPrompt(outer))
        {
            using (session.BeginInFlightPrompt(inner))
            {
                var innerResult = session.InjectTurnComment(inner.Id, "inner steer");
                if (innerResult.IsError)
                    throw new InvalidOperationException($"Inner inject should succeed, got '{innerResult.Error}'.");
                if (!session.IsTurnInFlight(inner.Id) || !session.IsTurnInFlight(outer.Id))
                    throw new InvalidOperationException("Both nested in-flight turns must report in-flight.");

                ExpectErrorMessage(
                    session.InjectTurnComment(Guid.NewGuid(), "wrong id"),
                    "Turn is not currently running.",
                    "nested stack wrong id");
            }

            if (!session.IsTurnInFlight(outer.Id))
                throw new InvalidOperationException("Outer turn must stay in-flight after inner scope ends.");
            if (session.IsTurnInFlight(inner.Id))
                throw new InvalidOperationException("Inner turn must leave the stack when its scope ends.");

            var outerResult = session.InjectTurnComment(outer.Id, "outer steer");
            if (outerResult.IsError)
                throw new InvalidOperationException($"Outer inject should succeed after inner pops, got '{outerResult.Error}'.");
        }

        if (session.IsTurnInFlight(outer.Id) || session.IsTurnInFlight(inner.Id))
            throw new InvalidOperationException("Nested scopes must pop both turns.");
    }

    private static void AssertDrainFormatsAndLeavesReasoningSegments()
    {
        var turn = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        ExpectSuccess(turn.EnqueueUserComment("first"), "drain first enqueue");
        ExpectSuccess(turn.EnqueueUserComment("second"), "drain second enqueue");

        if (turn.FormatInjectedUserCommentsForTranscript().Length == 0)
            throw new InvalidOperationException("Formatter must emit comments from ReasoningLog before drain.");

        var drained = turn.TryDequeueUserComments();
        if (drained.Length != 2 || drained[0] != "first" || drained[1] != "second")
            throw new InvalidOperationException("TryDequeueUserComments must drain FIFO trimmed text.");
        if (turn.HasPendingUserComments)
            throw new InvalidOperationException("Queue must be empty after drain.");
        if (turn.TryDequeueUserComments().Length != 0)
            throw new InvalidOperationException("Second drain must return an empty array.");

        if (turn.ReasoningLog.Count != 2
            || turn.ReasoningLog[0].Kind != DysonReasoningSegmentKind.UserComment
            || turn.ReasoningLog[1].Kind != DysonReasoningSegmentKind.UserComment)
        {
            throw new InvalidOperationException("Drain must leave UserComment reasoning segments.");
        }

        var formatted = turn.FormatInjectedUserCommentsForTranscript();
        var normalized = formatted.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains("USER INJECTED COMMENT: first", StringComparison.Ordinal)
            || !normalized.Contains("USER INJECTED COMMENT: second", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Formatter must keep USER INJECTED COMMENT blocks after drain, got '{formatted}'.");
        }

        var firstIdx = normalized.IndexOf("USER INJECTED COMMENT: first", StringComparison.Ordinal);
        var secondIdx = normalized.IndexOf("USER INJECTED COMMENT: second", StringComparison.Ordinal);
        if (secondIdx <= firstIdx)
            throw new InvalidOperationException("Formatter must emit comments in log order.");
        var between = normalized[firstIdx..secondIdx];
        if (!between.Contains("\n\n", StringComparison.Ordinal))
            throw new InvalidOperationException("Formatter must put a blank line between comment blocks.");

        if (new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal }.FormatInjectedUserCommentsForTranscript() != "")
            throw new InvalidOperationException("Formatter must return empty string when there are no comments.");
    }

    private static void AssertPersistRestoreUserComment()
    {
        var live = new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal };
        live.AppendReasoningRound(0, "alpha thought", "mid words", includeInterimText: true);
        ExpectSuccess(live.EnqueueUserComment("persisted steer"), "persist enqueue");

        var entity = DysonTurnPersistence.ToEntity(live, Guid.NewGuid(), sequence: 1);
        if (string.IsNullOrWhiteSpace(entity.ReasoningLogJson)
            || !entity.ReasoningLogJson.Contains("persisted steer", StringComparison.Ordinal)
            || !entity.ReasoningLogJson.Contains("\"kind\":2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ToEntity must serialize UserComment (kind 2) in ReasoningLogJson, got '{entity.ReasoningLogJson}'.");
        }

        if (entity.ReasoningText != "alpha thought")
            throw new InvalidOperationException("ToEntity ReasoningText must remain Thought join.");

        var restored = new DysonAgentTurn { Id = entity.Id, Kind = entity.Kind };
        restored.RestoreReasoningLog(
            DysonReasoningLogSerializer.DeserializeOrSynthesize(entity.ReasoningLogJson, entity.ReasoningText));

        if (restored.ReasoningLog.Count != 3
            || restored.ReasoningLog[0].Kind != DysonReasoningSegmentKind.Thought
            || restored.ReasoningLog[1].Kind != DysonReasoningSegmentKind.InterimText
            || restored.ReasoningLog[2].Kind != DysonReasoningSegmentKind.UserComment
            || restored.ReasoningLog[2].Text != "persisted steer"
            || restored.ReasoningLog[2].RoundIndex != 0
            || restored.ReasoningText != "alpha thought")
        {
            throw new InvalidOperationException("RestoreReasoningLog lost the UserComment segment.");
        }

        if (restored.HasPendingUserComments)
            throw new InvalidOperationException("Pending drain queue must not round-trip via ReasoningLogJson.");

        var formatted = restored.FormatInjectedUserCommentsForTranscript();
        if (!formatted.Contains("USER INJECTED COMMENT: persisted steer", StringComparison.Ordinal))
            throw new InvalidOperationException("Restored UserComment must still format for transcripts.");
    }

    private static void AssertCompletedTurnHistoryEmitsComments()
    {
        var session = new StubSession();
        var previous = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "previous instruction unique-hist",
            AssistantText = "previous assistant unique-hist",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        previous.AppendReasoningRound(0, "SECRET_THOUGHT_TOKEN", "SECRET_INTERIM_TOKEN", includeInterimText: true);
        ExpectSuccess(previous.EnqueueUserComment("steer the next round"), "completed-history enqueue");
        session.AddTurnForTest(previous);
        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "current in-flight unique-hist",
            StartedUtc = DateTime.UtcNow,
        });

        AssertCompletedHistoryUserContent(
            OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Messages,
            previous,
            "Completions");
        AssertCompletedHistoryUserContent(
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Input,
            previous,
            "Responses");
    }

    private static void AssertCompletedHistoryUserContent(
        JsonArray items,
        DysonAgentTurn previous,
        string label)
    {
        var content = FindTurnUserContent(items, previous.Id)
            ?? throw new InvalidOperationException($"{label}: missing [turnId] user message.");
        if (!content.Contains("previous instruction unique-hist", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: instruction must remain on the [turnId] user message.");
        }

        if (!content.Contains("USER INJECTED COMMENT: steer the next round", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: comments must sit on the [turnId] user message, not a separate role.");
        }

        var commentUserCount = CountUserMessagesContaining(items, "USER INJECTED COMMENT:");
        if (commentUserCount != 1)
        {
            throw new InvalidOperationException(
                $"{label}: expected comments on exactly one user message, got {commentUserCount}.");
        }

        var json = items.ToJsonString();
        foreach (var secret in new[] { "SECRET_THOUGHT_TOKEN", "SECRET_INTERIM_TOKEN" })
        {
            if (json.Contains(secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{label}: transcript must omit reasoning; found '{secret}'.");
            }
        }
    }

    private static void AssertInFlightDoesNotSpliceCommentsOntoInstruction()
    {
        var session = new StubSession();
        var live = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "live instruction unique-inflight",
            StartedUtc = DateTime.UtcNow,
        };
        ExpectSuccess(live.EnqueueUserComment("in-flight steer"), "in-flight enqueue");
        session.AddTurnForTest(live);
        var formatted = live.FormatInjectedUserCommentsForTranscript();

        AssertInFlightInstructionOmitsComments(
            OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Messages,
            live,
            "Completions null follow-up");
        AssertInFlightInstructionOmitsComments(
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Input,
            live,
            "Responses null follow-up");

        AssertInFlightFollowUpEmitsComments(
            OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: formatted,
                currentFilePaths: null,
                inFlightRounds: []).Messages,
            live,
            "Completions follow-up");
        AssertInFlightFollowUpEmitsComments(
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: formatted,
                currentFilePaths: null,
                inFlightRounds: []).Input,
            live,
            "Responses follow-up");
    }

    private static void AssertInFlightInstructionOmitsComments(
        JsonArray items,
        DysonAgentTurn live,
        string label)
    {
        var instruction = FindTurnUserContent(items, live.Id)
            ?? throw new InvalidOperationException($"{label}: missing in-flight [turnId] user message.");
        if (!instruction.Contains("live instruction unique-inflight", StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: instruction missing from in-flight user content.");
        if (instruction.Contains("USER INJECTED COMMENT:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: incomplete current must not splice comments onto the instruction user message.");
        }

        if (CountUserMessagesContaining(items, "USER INJECTED COMMENT:") != 0)
        {
            throw new InvalidOperationException(
                $"{label}: comments must not appear without currentUserPrompt.");
        }
    }

    private static void AssertInFlightFollowUpEmitsComments(
        JsonArray items,
        DysonAgentTurn live,
        string label)
    {
        var instruction = FindTurnUserContent(items, live.Id)
            ?? throw new InvalidOperationException($"{label}: missing in-flight [turnId] user message.");
        if (instruction.Contains("USER INJECTED COMMENT:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: follow-up comments must not merge into the instruction user message.");
        }

        var header = $"[turnId={live.Id:D}]";
        JsonObject? followUp = null;
        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;
            if (obj["role"]?.GetValue<string>() != "user")
                continue;

            var text = TryGetStringContent(obj);
            if (text is null
                || !text.Contains("USER INJECTED COMMENT: in-flight steer", StringComparison.Ordinal)
                || text.Contains(header, StringComparison.Ordinal))
            {
                continue;
            }

            followUp = obj;
            break;
        }

        if (followUp is null)
        {
            throw new InvalidOperationException(
                $"{label}: formatted comments must appear as a follow-up user message after history.");
        }
    }

    private static void AssertCompactedTurnKeepsCommentsOnInstruction()
    {
        const string compact = "[compact] Grep unique-compact-payload";
        var session = new StubSession();
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "run grep unique-compact",
            AssistantText = "done unique-compact",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            ToolHistoryOptimized = true,
            CompactToolHistory = compact,
        };
        ExpectSuccess(turn.EnqueueUserComment("compact-surviving steer"), "compact enqueue");
        session.AddTurnForTest(turn);

        AssertCompactedComments(
            OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Messages,
            turn,
            compact,
            "Completions");
        AssertCompactedComments(
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Input,
            turn,
            compact,
            "Responses");
    }

    private static void AssertCompactedComments(
        JsonArray items,
        DysonAgentTurn turn,
        string compact,
        string label)
    {
        var instruction = FindTurnUserContent(items, turn.Id)
            ?? throw new InvalidOperationException($"{label}: missing compacted [turnId] user message.");
        if (!instruction.Contains("run grep unique-compact", StringComparison.Ordinal)
            || !instruction.Contains("USER INJECTED COMMENT: compact-surviving steer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: compacted turns must keep comments on the instruction user message.");
        }

        if (instruction.Contains(compact, StringComparison.Ordinal)
            || instruction.Contains("[compact]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: comments must not live only inside the [compact] payload.");
        }

        JsonObject? compactMsg = null;
        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;
            var text = TryGetStringContent(obj);
            if (text is null || !text.Contains(compact, StringComparison.Ordinal))
                continue;
            compactMsg = obj;
            break;
        }

        if (compactMsg is null)
            throw new InvalidOperationException($"{label}: compact payload must still be present.");

        var compactRole = compactMsg["role"]?.GetValue<string>();
        var compactBody = TryGetStringContent(compactMsg) ?? "";
        if (!string.Equals(compactRole, "user", StringComparison.Ordinal)
            || !compactBody.StartsWith(
                OpenAiCacheFriendlyTranscriptBuilder.CompactToolHistoryHarnessPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: compact payload must still be present as a role=user harness summary.");
        }
    }

    private static void AssertSummarizedStubKeepsComments()
    {
        var session = new StubSession();
        var summarized = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "VERBOSE_INSTRUCTION_SHOULD_NOT_APPEAR",
            AssistantText = "VERBOSE_ASSISTANT_SHOULD_NOT_APPEAR",
            CompactToolHistory = "VERBOSE_TOOLS_SHOULD_NOT_APPEAR",
            ToolHistoryOptimized = true,
            ContextSummary = "compact facts only unique-sum",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        ExpectSuccess(summarized.EnqueueUserComment("summarized steer unique-sum"), "summarized enqueue");
        session.AddTurnForTest(summarized);
        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "continue after summary",
            StartedUtc = DateTime.UtcNow,
        });

        AssertSummarizedTranscript(
            OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Messages,
            summarized,
            "Completions");
        AssertSummarizedTranscript(
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []).Input,
            summarized,
            "Responses");
    }

    private static void AssertSummarizedTranscript(
        JsonArray items,
        DysonAgentTurn summarized,
        string label)
    {
        var json = items.ToJsonString();
        if (!json.Contains($"[turnId={summarized.Id:D}]", StringComparison.Ordinal)
            || !json.Contains("[contextSummary]", StringComparison.Ordinal)
            || !json.Contains("compact facts only unique-sum", StringComparison.Ordinal)
            || !json.Contains("USER INJECTED COMMENT: summarized steer unique-sum", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label}: summarized stub must include turnId + summary + comment blocks.");
        }

        foreach (var verbose in new[]
                 {
                     "VERBOSE_INSTRUCTION_SHOULD_NOT_APPEAR",
                     "VERBOSE_ASSISTANT_SHOULD_NOT_APPEAR",
                     "VERBOSE_TOOLS_SHOULD_NOT_APPEAR",
                 })
        {
            if (json.Contains(verbose, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{label}: summarized transcript must omit full body; found '{verbose}'.");
            }
        }

        var stub = FindTurnUserContent(items, summarized.Id)
            ?? throw new InvalidOperationException($"{label}: missing summarized [turnId] stub.");
        var summaryIdx = stub.IndexOf("compact facts only unique-sum", StringComparison.Ordinal);
        var commentIdx = stub.IndexOf("USER INJECTED COMMENT: summarized steer unique-sum", StringComparison.Ordinal);
        if (summaryIdx < 0 || commentIdx <= summaryIdx)
        {
            throw new InvalidOperationException(
                $"{label}: comment blocks must follow ContextSummary on the stub.");
        }
    }

    private static string? FindTurnUserContent(JsonArray items, Guid turnId)
    {
        var header = $"[turnId={turnId:D}]";
        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;
            if (obj["role"]?.GetValue<string>() != "user")
                continue;

            var text = TryGetStringContent(obj);
            if (text is not null && text.Contains(header, StringComparison.Ordinal))
                return text;
        }

        return null;
    }

    private static int CountUserMessagesContaining(JsonArray items, string needle)
    {
        var count = 0;
        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;
            if (obj["role"]?.GetValue<string>() != "user")
                continue;

            var text = TryGetStringContent(obj);
            if (text is not null && text.Contains(needle, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private static string? TryGetStringContent(JsonObject obj) =>
        obj["content"] is JsonValue value ? value.GetValue<string>() : obj["content"]?.ToJsonString();

    private static void ExpectSuccess(VoidResult<string> result, string caseName)
    {
        if (result.IsError)
        {
            throw new InvalidOperationException(
                $"{caseName}: expected success, got '{result.Error}'.");
        }
    }

    private static void ExpectError(VoidResult<string> result, string caseName)
    {
        if (result.IsSuccess)
            throw new InvalidOperationException($"{caseName}: expected error, got success.");
    }

    private static void ExpectErrorMessage(VoidResult<string> result, string expected, string caseName)
    {
        if (result.IsSuccess)
            throw new InvalidOperationException($"{caseName}: expected error '{expected}', got success.");
        if (!string.Equals(result.Error, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{caseName}: expected '{expected}', got '{result.Error}'.");
        }
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

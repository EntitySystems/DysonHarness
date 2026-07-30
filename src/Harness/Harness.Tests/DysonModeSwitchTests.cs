using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ModeSwitch: kind/display, append without inference, model-visible transcript boundary,
/// DisplayInfo still omitted; host-order ModeSwitch then Normal.
/// </summary>
public class DysonModeSwitchTests
{
    [Fact]
    public void Run()
    {
        AssertKindAndDisplayName();
        AssertAppendFields();
        AssertTranscriptIncludesModeSwitchOmitsDisplayInfo();
        AssertPromptPathOrderModeSwitchThenNormal();
    }

    private static void AssertKindAndDisplayName()
    {
        if ((int)DysonAgentTurnKind.ModeSwitch != 12)
            throw new InvalidOperationException("DysonAgentTurnKind.ModeSwitch must be 12.");

        var label = DysonAgentTurnKindDisplay.GetDisplayName(DysonAgentTurnKind.ModeSwitch);
        if (!string.Equals(label, "Mode switch", StringComparison.Ordinal))
            throw new InvalidOperationException($"ModeSwitch label expected 'Mode switch', got '{label}'.");
    }

    private static void AssertAppendFields()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var promptCalls = 0;
        session.OnPromptHarness = _ =>
        {
            promptCalls++;
            return Task.FromResult(VoidResult<string>.Success);
        };

        var turn = session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        if (turn.Kind != DysonAgentTurnKind.ModeSwitch
            || turn.Instruction != "Work→Plan"
            || turn.AssistantText != "Switched to Plan"
            || turn.CompletedUtc is null
            || session.Turns.Count != 1
            || !ReferenceEquals(session.Turns[0], turn))
        {
            throw new InvalidOperationException("AppendModeSwitchTurn fields / history mismatch.");
        }

        if (promptCalls != 0)
            throw new InvalidOperationException("AppendModeSwitchTurn must not call PromptHarnessTurnAsync.");
    }

    private static void AssertTranscriptIncludesModeSwitchOmitsDisplayInfo()
    {
        var session = new StubSession(DysonAgentModes.Plan);
        const string infoText = "Ask me anything";
        session.AppendDisplayInfoTurn(infoText);
        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "real user prompt",
            AssistantText = "real reply",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        });

        const string expectedHarness =
            "[Harness: agent mode switched from Work to Plan. Follow the current system instructions for Plan mode from this point on.]";

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        AssertTranscript(completions.Messages.ToJsonString(), expectedHarness, infoText, "Completions");

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        AssertTranscript(responses.Input.ToJsonString(), expectedHarness, infoText, "Responses");
    }

    private static void AssertPromptPathOrderModeSwitchThenNormal()
    {
        // Host ApplyAgentModeCoreAsync → AppendModeSwitchTurn then PromptAsync builds Normal.
        var session = new StubSession(DysonAgentModes.Work);
        var applied = session.ApplyAgentMode(DysonAgentModes.Plan);
        if (applied.IsError)
            throw new InvalidOperationException($"ApplyAgentMode failed: {applied.Error}");

        session.AppendModeSwitchTurn(DysonAgentModes.Work, DysonAgentModes.Plan);
        var user = DysonAgentSession.CreateNormalTurn("build the feature");
        session.AddTurnForTest(user);

        if (session.Turns.Count != 2
            || session.Turns[0].Kind != DysonAgentTurnKind.ModeSwitch
            || session.Turns[1].Kind != DysonAgentTurnKind.Normal
            || session.Turns[1].Instruction != "build the feature")
        {
            throw new InvalidOperationException(
                "Prompt path order must be ModeSwitch then Normal user turn.");
        }

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        var json = completions.Messages.ToJsonString();
        var harnessIdx = json.IndexOf(
            "[Harness: agent mode switched from Work to Plan.",
            StringComparison.Ordinal);
        var userIdx = json.IndexOf("build the feature", StringComparison.Ordinal);
        if (harnessIdx < 0 || userIdx < 0 || harnessIdx >= userIdx)
        {
            throw new InvalidOperationException(
                "Transcript must emit ModeSwitch harness user message before the Normal user turn.");
        }

        if (json.Contains("Switched to Plan", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ModeSwitch UI banner must not appear as an assistant transcript message.");
        }
    }

    private static void AssertTranscript(
        string json,
        string expectedHarness,
        string infoText,
        string label)
    {
        if (json.Contains(infoText, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} transcript must omit DisplayInfo text.");

        if (!json.Contains(expectedHarness, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} transcript must include ModeSwitch harness message.");

        if (json.Contains("Switched to Plan", StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} must not emit ModeSwitch AssistantText as assistant.");

        if (!json.Contains("real user prompt", StringComparison.Ordinal)
            || !json.Contains("real reply", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} transcript must still include Normal turns.");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public Func<DysonAgentTurn, Task<VoidResult<string>>>? OnPromptHarness { get; set; }

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
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => OnPromptHarness?.Invoke(turn)
               ?? Task.FromResult(VoidResult<string>.Success);

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

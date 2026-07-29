using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// DisplayInfo is UI-only chrome: kind value, display name, transcript omit, append without inference.
/// </summary>
public class DysonDisplayInfoTests
{
    [Fact]
    public void Run()
    {
        AssertKindAndDisplayName();
        AssertAppendFields();
        AssertTranscriptOmitsDisplayInfo();
    }

    private static void AssertKindAndDisplayName()
    {
        if ((int)DysonAgentTurnKind.DisplayInfo != 11)
            throw new InvalidOperationException("DysonAgentTurnKind.DisplayInfo must be 11.");

        var label = DysonAgentTurnKindDisplay.GetDisplayName(DysonAgentTurnKind.DisplayInfo);
        if (!string.Equals(label, "Info", StringComparison.Ordinal))
            throw new InvalidOperationException($"DisplayInfo label expected 'Info', got '{label}'.");
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

        var turn = session.AppendDisplayInfoTurn("Let's harness your ideas, tell me your goals");
        if (turn.Kind != DysonAgentTurnKind.DisplayInfo
            || turn.AssistantText != "Let's harness your ideas, tell me your goals"
            || !string.IsNullOrEmpty(turn.Instruction)
            || turn.CompletedUtc is null
            || session.Turns.Count != 1
            || !ReferenceEquals(session.Turns[0], turn))
        {
            throw new InvalidOperationException("AppendDisplayInfoTurn fields / history mismatch.");
        }

        if (promptCalls != 0)
            throw new InvalidOperationException("AppendDisplayInfoTurn must not call PromptHarnessTurnAsync.");
    }

    private static void AssertTranscriptOmitsDisplayInfo()
    {
        var session = new StubSession(DysonAgentModes.Work);
        const string infoText = "Ask me anything";
        session.AppendDisplayInfoTurn(infoText);
        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "real user prompt",
            AssistantText = "real reply",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        });

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        AssertOmitsInfo(completions.Messages.ToJsonString(), infoText, "Completions");

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        AssertOmitsInfo(responses.Input.ToJsonString(), infoText, "Responses");
    }

    private static void AssertOmitsInfo(string json, string infoText, string label)
    {
        if (json.Contains(infoText, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} transcript must omit DisplayInfo text.");

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

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// WorktreeCreating is UI-only chrome: kind 17, display name, incomplete begin,
/// complete/fail stamp, transcript omit (same family as DisplayInfo).
/// </summary>
public class DysonWorktreeCreatingTests
{
    [Fact]
    public void Run()
    {
        AssertKindAndDisplayName();
        AssertBeginFields();
        AssertCompleteBanner();
        AssertFailError();
        AssertTranscriptOmitsWorktreeCreating();
    }

    private static void AssertKindAndDisplayName()
    {
        if ((int)DysonAgentTurnKind.WorktreeCreating != 17)
            throw new InvalidOperationException("DysonAgentTurnKind.WorktreeCreating must be 17.");

        var label = DysonAgentTurnKindDisplay.GetDisplayName(DysonAgentTurnKind.WorktreeCreating);
        if (!string.Equals(label, "Creating worktree", StringComparison.Ordinal))
            throw new InvalidOperationException($"WorktreeCreating label expected 'Creating worktree', got '{label}'.");

        if (!DysonAgentTurnKindRules.AllowsEnqueue(DysonAgentTurnKind.WorktreeCreating))
            throw new InvalidOperationException("WorktreeCreating must allow enqueue (not TaskEndReflect).");
    }

    private static void AssertBeginFields()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var promptCalls = 0;
        session.OnPromptHarness = _ =>
        {
            promptCalls++;
            return Task.FromResult(VoidResult<string>.Success);
        };

        var turn = session.BeginWorktreeCreatingTurn();
        if (turn.Kind != DysonAgentTurnKind.WorktreeCreating
            || turn.CompletedUtc is not null
            || turn.AgentTitle != "Creating worktree…"
            || turn.AssistantText != "Creating worktree…"
            || session.Turns.Count != 1
            || !ReferenceEquals(session.Turns[0], turn))
        {
            throw new InvalidOperationException("BeginWorktreeCreatingTurn fields / history mismatch.");
        }

        if (promptCalls != 0)
            throw new InvalidOperationException("BeginWorktreeCreatingTurn must not call PromptHarnessTurnAsync.");
    }

    private static void AssertCompleteBanner()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var turn = session.BeginWorktreeCreatingTurn();
        session.CompleteWorktreeCreatingTurn(turn, @"C:\repo.dyson-worktrees\abc", "dyson/abcdef01");

        if (turn.CompletedUtc is null)
            throw new InvalidOperationException("CompleteWorktreeCreatingTurn must stamp CompletedUtc.");

        if (turn.AgentTitle != "Creating worktree…")
            throw new InvalidOperationException("CompleteWorktreeCreatingTurn must keep AgentTitle.");

        var text = turn.AssistantText ?? "";
        if (!text.Contains("Worktree ready", StringComparison.Ordinal)
            || !text.Contains("`dyson/abcdef01`", StringComparison.Ordinal)
            || !text.Contains(@"`C:\repo.dyson-worktrees\abc`", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Ready banner expected path+branch, got '{text}'.");
        }
    }

    private static void AssertFailError()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var turn = session.BeginWorktreeCreatingTurn();
        const string error = "Worktree is enabled but this work directory is not a git repository.";
        session.FailWorktreeCreatingTurn(turn, error);

        if (turn.CompletedUtc is null)
            throw new InvalidOperationException("FailWorktreeCreatingTurn must stamp CompletedUtc.");

        if (turn.AssistantText != error)
            throw new InvalidOperationException($"Fail AssistantText expected error, got '{turn.AssistantText}'.");
    }

    private static void AssertTranscriptOmitsWorktreeCreating()
    {
        var session = new StubSession(DysonAgentModes.Work);
        const string creatingText = "Creating worktree…";
        const string path = "/tmp/repo.dyson-worktrees/sid";
        const string branch = "dyson/deadbeef";
        var chrome = session.BeginWorktreeCreatingTurn();
        session.CompleteWorktreeCreatingTurn(chrome, path, branch);
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
        AssertOmitsChrome(completions.Messages.ToJsonString(), creatingText, path, branch, "Completions");

        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: []);
        AssertOmitsChrome(responses.Input.ToJsonString(), creatingText, path, branch, "Responses");
    }

    private static void AssertOmitsChrome(
        string json,
        string creatingText,
        string path,
        string branch,
        string label)
    {
        if (json.Contains(creatingText, StringComparison.Ordinal)
            || json.Contains(path, StringComparison.Ordinal)
            || json.Contains(branch, StringComparison.Ordinal)
            || json.Contains("Worktree ready", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} transcript must omit WorktreeCreating text.");
        }

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
            IReadOnlyList<string>? contextFiles = null,
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

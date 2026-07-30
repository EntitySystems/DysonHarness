using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: PromptUserDialog parse/format + session pending/respond + layer gating (Xunit Fact).
/// </summary>
public class DysonPromptUserDialogTests
{
    [Fact]
    public void Run()
    {
        AssertParseAndFormat();
        AssertLayerGating();
        AssertSessionPendingAndRespond().GetAwaiter().GetResult();
        AssertMutualExclusionWithAsk().GetAwaiter().GetResult();
    }

    private static void AssertParseAndFormat()
    {
        var ok = DysonPromptUserDialog.ParseDialogJson(
            """
            {
              "title": "Next step",
              "description": "How should we proceed?",
              "actions": [
                { "label": "Continue", "primary": true },
                { "label": "Stop" }
              ]
            }
            """);
        if (ok.IsError
            || ok.Value.Actions.Count != 2
            || !ok.Value.Actions[0].Primary
            || ok.Value.Actions[1].Primary)
        {
            throw new InvalidOperationException("ParseDialogJson failed: " + (ok.IsError ? ok.Error : "shape"));
        }

        var empty = DysonPromptUserDialog.ParseDialogJson(
            """{"title":"T","description":"D","actions":[]}""");
        if (!empty.IsError)
            throw new InvalidOperationException("Expected empty actions rejected.");

        var tooMany = DysonPromptUserDialog.ParseDialogJson(
            """
            {
              "title": "T",
              "description": "D",
              "actions": [
                {"label":"a"},{"label":"b"},{"label":"c"},{"label":"d"},{"label":"e"}
              ]
            }
            """);
        if (!tooMany.IsError || tooMany.Error.IndexOf("4", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("Expected max-4 actions validation.");

        var dualPrimary = DysonPromptUserDialog.ParseDialogJson(
            """
            {
              "title": "T",
              "description": "D",
              "actions": [
                {"label":"a","primary":true},
                {"label":"b","primary":true}
              ]
            }
            """);
        if (!dualPrimary.IsError || dualPrimary.Error.IndexOf("primary", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Expected dual-primary rejected.");

        var reservedSkip = DysonPromptUserDialog.ParseDialogJson(
            """
            {
              "title": "T",
              "description": "D",
              "actions": [{ "label": "Skip" }]
            }
            """);
        if (!reservedSkip.IsError)
            throw new InvalidOperationException("Expected reserved Skip label rejected.");

        var chosen = DysonPromptUserDialog.FormatResult("Continue", skipped: false);
        using (var doc = JsonDocument.Parse(chosen))
        {
            if (doc.RootElement.GetProperty("action").GetString() != "Continue"
                || doc.RootElement.GetProperty("skipped").GetBoolean())
            {
                throw new InvalidOperationException("FormatResult chosen shape wrong: " + chosen);
            }
        }

        var skipped = DysonPromptUserDialog.FormatResult("ignored", skipped: true);
        using (var doc = JsonDocument.Parse(skipped))
        {
            if (doc.RootElement.GetProperty("action").GetString() != DysonPromptUserDialog.SkipActionLabel
                || !doc.RootElement.GetProperty("skipped").GetBoolean()
                || string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("guidance").GetString()))
            {
                throw new InvalidOperationException("FormatResult Skip shape wrong: " + skipped);
            }
        }
    }

    private static void AssertLayerGating()
    {
        var root = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        root.ConfigureInterAgentTools(0);
        AssertHas(root, "PromptUserDialog");
        AssertMissing(root, "PromptUserDialogFromParent");

        var l1 = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        l1.ConfigureInterAgentTools(1);
        AssertMissing(l1, "PromptUserDialog");
        AssertHas(l1, "PromptUserDialogFromParent");

        var deep = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        deep.ConfigureInterAgentTools(2);
        AssertMissing(deep, "PromptUserDialog");
        AssertMissing(deep, "PromptUserDialogFromParent");
    }

    private static async Task AssertSessionPendingAndRespond()
    {
        var session = new StubSession();
        const string json =
            """
            {
              "title": "Ship?",
              "description": "Ready to publish?",
              "actions": [{ "label": "Publish", "primary": true }, { "label": "Hold" }]
            }
            """;

        var task = session.PromptUserDialogAsync(json, CancellationToken.None);
        await Task.Delay(25).ConfigureAwait(false);

        if (session.PendingUserDialog is null
            || session.PendingUserDialog.Actions.Count != 2)
        {
            throw new InvalidOperationException("Expected PendingUserDialog while blocked.");
        }

        var formatted = DysonPromptUserDialog.FormatResult("Publish", skipped: false);
        var respond = session.RespondToPromptUserDialog(formatted);
        if (respond.IsError)
            throw new InvalidOperationException("RespondToPromptUserDialog failed: " + respond.Error);

        var result = await task.ConfigureAwait(false);
        if (result.IsError || !result.Value.Contains("Publish", StringComparison.Ordinal))
            throw new InvalidOperationException("PromptUserDialog result wrong: " + (result.IsError ? result.Error : result.Value));

        if (session.PendingUserDialog is not null)
            throw new InvalidOperationException("PendingUserDialog should clear after respond.");
    }

    private static async Task AssertMutualExclusionWithAsk()
    {
        var session = new StubSession();
        var askTask = session.AskQuestionAsync(
            """{"questions":[{"prompt":"P","options":["a"]}]}""",
            CancellationToken.None);
        await Task.Delay(25).ConfigureAwait(false);

        var dialog = await session.PromptUserDialogAsync(
            """{"title":"T","description":"D","actions":[{"label":"Go"}]}""",
            CancellationToken.None).ConfigureAwait(false);
        if (!dialog.IsError || dialog.Error.IndexOf("AskQuestion", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Expected PromptUserDialog rejected while AskQuestion pending: " +
                (dialog.IsError ? dialog.Error : dialog.Value));
        }

        session.RespondToAskQuestion("A1 - a");
        await askTask.ConfigureAwait(false);
    }

    private static void AssertHas(DysonMcpPipeline pipeline, string name)
    {
        if (!pipeline.Tools.ContainsKey(name))
            throw new InvalidOperationException($"Expected tool {name} in catalog.");
    }

    private static void AssertMissing(DysonMcpPipeline pipeline, string name)
    {
        if (pipeline.Tools.ContainsKey(name))
            throw new InvalidOperationException($"Did not expect tool {name} in catalog.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
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

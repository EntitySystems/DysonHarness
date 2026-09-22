using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Conversation actions: in-process func by key, and persist of the key only.
/// </summary>
public class DysonConversationActionTests
{
    [Fact]
    public async Task Invoke()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var calls = 0;
        var registered = session.RegisterConversationAction(
            "ping",
            _ =>
            {
                calls++;
                return Task.FromResult(Result<string, string>.AsValue("pong"));
            });
        Assert.False(registered.IsError);

        var turn = session.AppendDisplayInfoTurn(
            "hello",
            [new DysonConversationAction("Ping", "ping")]);
        var id = turn.Id;
        var count = session.Turns.Count;

        var ok = await session.InvokeConversationActionAsync("ping", CancellationToken.None);
        Assert.False(ok.IsError);
        Assert.Equal("pong", ok.Value);
        Assert.Equal(1, calls);

        var missing = await session.InvokeConversationActionAsync("missing", CancellationToken.None);
        Assert.True(missing.IsError);

        var thrown = session.RegisterConversationAction(
            "boom",
            _ => throw new InvalidOperationException("boom"));
        Assert.False(thrown.IsError);
        var boom = await session.InvokeConversationActionAsync("boom", CancellationToken.None);
        Assert.True(boom.IsError);
        Assert.Contains("boom", boom.Error);

        Assert.Equal(count, session.Turns.Count);
        Assert.Equal(id, session.Turns[0].Id);
        Assert.Same(turn, session.Turns[0]);
    }

    [Fact]
    public void PersistTheKeyOnly()
    {
        var session = new StubSession(DysonAgentModes.Work);
        const string text = "hello";
        var turn = session.AppendDisplayInfoTurn(
            text,
            [new DysonConversationAction("Open", "open-it")]);
        Assert.Equal(text, turn.AssistantText);

        var entity = DysonTurnPersistence.ToEntity(turn, Guid.NewGuid(), sequence: 1);
        var json = entity.ConversationActionsJson;
        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.Contains("\"func\"", json, StringComparison.Ordinal);
        Assert.Contains("open-it", json, StringComparison.Ordinal);
        Assert.Contains("Open", json, StringComparison.Ordinal);
        Assert.DoesNotContain("funcKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Func", json, StringComparison.Ordinal);
        Assert.Equal(text, turn.AssistantText);

        var restored = new DysonAgentTurn();
        restored.RestoreConversationActions(DysonConversationActionsSerializer.Deserialize(json));
        var action = Assert.Single(restored.ConversationActions);
        Assert.Equal("Open", action.Name);
        Assert.Equal("open-it", action.FuncKey);

        Assert.Empty(DysonConversationActionsSerializer.Deserialize(null));
        Assert.Empty(DysonConversationActionsSerializer.Deserialize(""));
        Assert.Empty(DysonConversationActionsSerializer.Deserialize("   "));
        Assert.Empty(DysonConversationActionsSerializer.Deserialize("{"));

        var skipped = DysonConversationActionsSerializer.Deserialize(
            """[{"name":" ","func":"x"},{"name":"Ok","func":"k","extra":1}]""");
        var kept = Assert.Single(skipped);
        Assert.Equal("Ok", kept.Name);
        Assert.Equal("k", kept.FuncKey);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
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
            => Task.FromResult(VoidResult<string>.Success);

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

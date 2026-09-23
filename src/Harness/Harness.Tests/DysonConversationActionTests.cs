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

    [Fact]
    public async Task Open_plan_accepts_positive_id()
    {
        var twelve = await DysonBuiltInConversationActions.TryResolveAsync(
            "open_plan:12", null, CancellationToken.None);
        Assert.NotNull(twelve);
        Assert.True(twelve!.IsSuccess, twelve.Error);
        Assert.Equal(DysonBuiltInConversationKind.OpenPlan, twelve.Value.Kind);
        Assert.Equal(12, twelve.Value.PlanId);

        var padded = await DysonBuiltInConversationActions.TryResolveAsync(
            "open_plan:01", null, CancellationToken.None);
        Assert.NotNull(padded);
        Assert.True(padded!.IsSuccess, padded.Error);
        Assert.Equal(1, padded.Value.PlanId);

        Assert.False(DysonBuiltInConversationActions.IsReservedPrefix("OPEN_PLAN:1"));
        Assert.Null(await DysonBuiltInConversationActions.TryResolveAsync(
            "OPEN_PLAN:1", null, CancellationToken.None));
    }

    [Fact]
    public async Task Open_plan_rejects_bad_id()
    {
        foreach (var key in new[] { "open_plan:", "open_plan:0", "open_plan:-1", "open_plan:nope", "open_plan:1.5", "open_plan:+1" })
        {
            var resolved = await DysonBuiltInConversationActions.TryResolveAsync(key, null, CancellationToken.None);
            Assert.NotNull(resolved);
            Assert.True(resolved!.IsError);
            Assert.Equal(DysonMetaAgentTools.PlanIdMustBePositiveMessage, resolved.Error);
        }
    }

    [Fact]
    public async Task Open_file_accepts_relative_path()
    {
        var root = CreateTempDir();
        try
        {
            var resolved = await DysonBuiltInConversationActions.TryResolveAsync(
                "open_file:src/A.cs", root, CancellationToken.None);
            Assert.NotNull(resolved);
            Assert.True(resolved!.IsSuccess, resolved.Error);
            Assert.Equal(DysonBuiltInConversationKind.OpenFile, resolved.Value.Kind);
            Assert.Equal("src/A.cs", resolved.Value.PathOrUrl);
            Assert.False(File.Exists(Path.Combine(root, "src", "A.cs")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Open_file_rejects_escape()
    {
        var root = CreateTempDir();
        try
        {
            var rooted = Path.Combine(root, "A.cs");
            foreach (var key in new[]
            {
                "open_file:../outside.txt",
                "open_file:src/../A.cs",
                "open_file:" + rooted,
            })
            {
                var resolved = await DysonBuiltInConversationActions.TryResolveAsync(
                    key, root, CancellationToken.None);
                Assert.NotNull(resolved);
                Assert.True(resolved!.IsError);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Open_url_accepts_https()
    {
        foreach (var (key, url) in new[]
        {
            ("open_url:https://example.com/a", "https://example.com/a"),
            ("open_url:http://127.0.0.1/x", "http://127.0.0.1/x"),
        })
        {
            var resolved = await DysonBuiltInConversationActions.TryResolveAsync(key, null, CancellationToken.None);
            Assert.NotNull(resolved);
            Assert.True(resolved!.IsSuccess, resolved.Error);
            Assert.Equal(DysonBuiltInConversationKind.OpenUrl, resolved.Value.Kind);
            Assert.Equal(url, resolved.Value.PathOrUrl);
        }
    }

    [Fact]
    public async Task Open_url_rejects_non_http()
    {
        foreach (var key in new[]
        {
            "open_url:javascript:alert(1)",
            "open_url:file:///c:/x",
            "open_url:ftp://example.com",
            "open_url:example.com",
        })
        {
            var resolved = await DysonBuiltInConversationActions.TryResolveAsync(key, null, CancellationToken.None);
            Assert.NotNull(resolved);
            Assert.True(resolved!.IsError);
        }
    }

    [Fact]
    public async Task Unknown_key_still_not_registered()
    {
        var session = new StubSession(DysonAgentModes.Work);
        var missing = await session.InvokeConversationActionAsync("missing", CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Contains("is not registered", missing.Error, StringComparison.Ordinal);

        var calls = 0;
        var registered = session.RegisterConversationAction(
            "ping",
            _ =>
            {
                calls++;
                return Task.FromResult(Result<string, string>.AsValue("pong"));
            });
        Assert.False(registered.IsError);
        var ok = await session.InvokeConversationActionAsync("ping", CancellationToken.None);
        Assert.False(ok.IsError);
        Assert.Equal("pong", ok.Value);
        Assert.Equal(1, calls);

        var reservedCalls = 0;
        var reservedReg = session.RegisterConversationAction(
            "open_plan:1",
            _ =>
            {
                reservedCalls++;
                return Task.FromResult(Result<string, string>.AsValue("nope"));
            });
        Assert.False(reservedReg.IsError);
        var reserved = await session.InvokeConversationActionAsync("open_plan:1", CancellationToken.None);
        Assert.True(reserved.IsError);
        Assert.Contains("reserved", reserved.Error, StringComparison.Ordinal);
        Assert.Equal(0, reservedCalls);

        Assert.Null(await DysonBuiltInConversationActions.TryResolveAsync("ping", null, CancellationToken.None));
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-conv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore cleanup races
        }
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

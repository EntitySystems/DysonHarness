using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// ponytail: assert-only self-check for restore registration / next-id bump and ListSubagents JSON shape.
/// Run: <c>DysonSubagentRestoreSelfCheck.Run()</c> (also from UI <c>Program</c> startup).
/// </summary>
public static class DysonSubagentRestoreSelfCheck
{
    public static void Run()
    {
        AssertRestoreRegistrationAndIdBump();
        AssertListSubagentsJsonShape();
    }

    private static void AssertRestoreRegistrationAndIdBump()
    {
        var parent = new StubSession();
        var childA = new StubSession();
        var childB = new StubSession();

        childA.RestoreForTest(MakePersisted(Guid.NewGuid(), runtimeId: 2, title: "Explore A"));
        childB.RestoreForTest(MakePersisted(Guid.NewGuid(), runtimeId: 5, title: "Drone B"));

        parent.RestoreRegisteredSubagent(childA);
        parent.RestoreRegisteredSubagent(childB);

        if (!parent.TryGetSubagent(2, out var gotA) || !ReferenceEquals(gotA, childA))
            throw new InvalidOperationException("Expected restored subagent id 2.");
        if (!parent.TryGetSubagent(5, out var gotB) || !ReferenceEquals(gotB, childB))
            throw new InvalidOperationException("Expected restored subagent id 5.");
        if (!ReferenceEquals(childA.Parent, parent) || !ReferenceEquals(childB.Parent, parent))
            throw new InvalidOperationException("Expected Parent to be wired on restore.");

        // Idempotent re-link
        parent.RestoreRegisteredSubagent(childA);

        var fresh = new StubSession();
        parent.RegisterForTest(fresh);
        if (fresh.Id != 6)
            throw new InvalidOperationException($"Expected next allocated id 6 after bump to 5, got {fresh.Id}.");
    }

    private static void AssertListSubagentsJsonShape()
    {
        var parent = new StubSession();
        var child = new StubSession();
        var persistenceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        child.RestoreForTest(MakePersisted(persistenceId, runtimeId: 1, title: "Child One"));
        parent.RestoreRegisteredSubagent(child);

        using var doc = JsonDocument.Parse(parent.FormatListSubagentsJson());
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != 1)
            throw new InvalidOperationException("ListSubagents JSON must be a one-element array.");

        var item = doc.RootElement[0];
        if (item.GetProperty("subagentId").GetInt32() != 1)
            throw new InvalidOperationException("Expected subagentId 1.");
        if (!string.Equals(item.GetProperty("persistenceId").GetString(), persistenceId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Expected persistenceId to match.");
        if (!string.Equals(item.GetProperty("agentMode").GetString(), DysonAgentModes.Work, StringComparison.Ordinal))
            throw new InvalidOperationException("Expected agentMode.");
        if (!string.Equals(item.GetProperty("title").GetString(), "Child One", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected title.");
        if (!string.Equals(item.GetProperty("status").GetString(), nameof(DysonSessionStatus.Active), StringComparison.Ordinal))
            throw new InvalidOperationException("Expected status Active.");
        if (!item.TryGetProperty("modelLabel", out _))
            throw new InvalidOperationException("Expected modelLabel property (nullable).");
    }

    private static DysonPersistedSession MakePersisted(Guid id, int runtimeId, string title) =>
        new()
        {
            Session = new DysonSessionEntity
            {
                Id = id,
                RuntimeId = runtimeId,
                AgentMode = DysonAgentModes.Work,
                Status = DysonSessionStatus.Active,
                Title = title,
                SystemPromptSnapshot = "test",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow,
            },
            Turns = [],
            Logs = [],
            Todos = [],
        };

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void RestoreForTest(DysonPersistedSession state) => RestoreFromPersisted(state);

        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

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
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

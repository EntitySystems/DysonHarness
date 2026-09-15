using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: concurrent WriteFile same-path edits must compose via the per-path gate.
/// </summary>
public class DysonWriteFileExecutorTests
{
    [Fact]
    public async Task Concurrent_same_path_targeted_edits_compose()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "note.txt"), "AAA\nBBB\n");
            using var http = new HttpClient();
            var first = await DysonWorkspaceTestFs.CreateExecutorAsync(new StubSession(), root, http);
            var second = await DysonWorkspaceTestFs.CreateExecutorAsync(new StubSession(), root, http);

            var editAaa = WriteFileAsync(first, """{"path":"note.txt","old_text":"AAA","new_text":"aaa"}""");
            var editBbb = WriteFileAsync(second, """{"path":"note.txt","old_text":"BBB","new_text":"bbb"}""");
            await Task.WhenAll(editAaa, editBbb);

            if (editAaa.Result.IsError)
                throw new InvalidOperationException($"AAA edit failed: {editAaa.Result.Content}");
            if (editBbb.Result.IsError)
                throw new InvalidOperationException($"BBB edit failed: {editBbb.Result.Content}");

            var onDisk = File.ReadAllText(Path.Combine(root, "note.txt"));
            if (onDisk != "aaa\nbbb\n")
                throw new InvalidOperationException($"Expected aaa\\nbbb\\n after concurrent edits, got: {JsonSerializer.Serialize(onDisk)}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Concurrent_different_path_content_writes_succeed()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "old-a");
            File.WriteAllText(Path.Combine(root, "b.txt"), "old-b");
            using var http = new HttpClient();
            var first = await DysonWorkspaceTestFs.CreateExecutorAsync(new StubSession(), root, http);
            var second = await DysonWorkspaceTestFs.CreateExecutorAsync(new StubSession(), root, http);

            var writeA = WriteFileAsync(first, """{"path":"a.txt","content":"new-a"}""");
            var writeB = WriteFileAsync(second, """{"path":"b.txt","content":"new-b"}""");
            await Task.WhenAll(writeA, writeB);

            if (writeA.Result.IsError)
                throw new InvalidOperationException($"a.txt write failed: {writeA.Result.Content}");
            if (writeB.Result.IsError)
                throw new InvalidOperationException($"b.txt write failed: {writeB.Result.Content}");

            if (File.ReadAllText(Path.Combine(root, "a.txt")) != "new-a")
                throw new InvalidOperationException("a.txt content mismatch.");
            if (File.ReadAllText(Path.Combine(root, "b.txt")) != "new-b")
                throw new InvalidOperationException("b.txt content mismatch.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static Task<DysonToolCallResult> WriteFileAsync(
        DysonWorkspaceToolExecutor executor,
        string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = "WriteFile",
            Stage = 0,
            ArgumentsJson = argumentsJson,
        });

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-writefile-" + Guid.NewGuid().ToString("N"));
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
            // best-effort temp cleanup
        }
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
            IReadOnlyList<string>? contextFiles = null,
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

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
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

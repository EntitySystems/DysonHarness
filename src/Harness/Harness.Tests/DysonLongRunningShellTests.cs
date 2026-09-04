using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only long-running shell id allocation + abort + list/subscribe/ShellExited (Xunit Fact).
/// /// </summary>
public class DysonLongRunningShellTests
{
    [Fact]
    public async Task Run()
    {
        AssertCatalogGate();
        AssertShellExitedPromptAndTrim();
        AssertOutcomeMapping();
        AssertIdAllocationListAndAbort();
        await AssertListAndCountFilterByWorkingDirectory();
        await AssertStartIncludesEarlyOutput();
        await AssertSubscribeRootOnly();
        await AssertWaitForTimeoutValidation();
        await AssertWaitForAlreadyExitedImmediate();
        await AssertWaitForLiveTimeoutAndShortCommand();
    }

    private static void AssertCatalogGate()
    {
        var none = DysonMcpPipeline.CreateLongRunningShellTools([]).ToArray();
        if (none.Length != 0)
            throw new InvalidOperationException("Long-running shell tools must be omitted when no shells available.");

        var tools = DysonMcpPipeline.CreateLongRunningShellTools(["Pwsh"], planMode: false).ToArray();
        if (tools.Length != 8)
            throw new InvalidOperationException($"Expected 8 long-running shell tools, got {tools.Length}.");

        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "StartLongRunningShell",
                     "ListLongRunningShells",
                     "ReadLongRunningShellTail",
                     "AbortLongRunningShell",
                     "RequestLongRunningShellCancellation",
                     "LongRunningShellInteract",
                     "SubscribeToLongRunningShellCompletion",
                     "WaitForLongRunningShellCompletion",
                 })
        {
            if (!names.Contains(required))
                throw new InvalidOperationException($"Missing long-running shell tool '{required}'.");
        }

        var waitFor = tools.First(t => t.Name == "WaitForLongRunningShellCompletion");
        if (!waitFor.InputSchemaJson.Contains("\"longRunningShellId\"", StringComparison.Ordinal)
            || !waitFor.InputSchemaJson.Contains("\"timeoutMs\"", StringComparison.Ordinal)
            || !waitFor.InputSchemaJson.Contains("\"required\": [\"longRunningShellId\", \"timeoutMs\"]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "WaitForLongRunningShellCompletion schema must require longRunningShellId and timeoutMs.");
        }

        var start = tools.First(t => t.Name == "StartLongRunningShell");
        if (!start.Description.Contains("E2E", StringComparison.Ordinal)
            || !start.Description.Contains("ListLongRunningShells", StringComparison.Ordinal)
            || !start.Description.Contains("WaitForLongRunningShellCompletion", StringComparison.Ordinal)
            || !start.Description.Contains("SubscribeToLongRunningShellCompletion", StringComparison.Ordinal)
            || !start.Description.Contains("~1s", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "StartLongRunningShell description must mention E2E, List/WaitFor/Subscribe tools, and ~1s output.");
        }

        var root = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, ["Pwsh"]);
        root.ConfigureInterAgentTools(0);
        if (!root.Tools.ContainsKey("SubscribeToLongRunningShellCompletion")
            || !root.Tools.ContainsKey("WaitForLongRunningShellCompletion"))
        {
            throw new InvalidOperationException("Root catalog must include Subscribe and WaitFor long-running shell tools.");
        }

        var l1 = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, ["Pwsh"]);
        l1.ConfigureInterAgentTools(1);
        if (l1.Tools.ContainsKey("SubscribeToLongRunningShellCompletion")
            || !l1.Tools.ContainsKey("WaitForLongRunningShellCompletion"))
        {
            throw new InvalidOperationException("L1 catalog must omit Subscribe and keep WaitFor.");
        }

        var deep = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, ["Pwsh"]);
        deep.ConfigureInterAgentTools(2);
        if (deep.Tools.ContainsKey("SubscribeToLongRunningShellCompletion")
            || !deep.Tools.ContainsKey("WaitForLongRunningShellCompletion"))
        {
            throw new InvalidOperationException("Deep catalog must omit Subscribe and keep WaitFor.");
        }

        var noShells = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess, []);
        noShells.ConfigureLongRunningShellTools(planMode: false);
        if (noShells.Tools.ContainsKey("SubscribeToLongRunningShellCompletion")
            || noShells.Tools.ContainsKey("WaitForLongRunningShellCompletion"))
        {
            throw new InvalidOperationException("No-shell catalog must omit Subscribe and WaitFor.");
        }
    }

    private static void AssertShellExitedPromptAndTrim()
    {
        if ((int)DysonAgentTurnKind.ShellExited != 9)
            throw new InvalidOperationException("DysonAgentTurnKind.ShellExited must be 9.");

        const string fatTail = "BUILD FAILED: sample-error-token-xyz\nline2\nline3";
        var turn = DysonLongRunningShellExitedFlow.CreateTurn(
            id: 42,
            outcome: "failure",
            exitCode: 1,
            shell: "Cmd",
            command: "dotnet build",
            cwd: @"C:\tmp",
            tail: fatTail);

        if (turn.Kind != DysonAgentTurnKind.ShellExited)
            throw new InvalidOperationException("CreateTurn must set Kind=ShellExited.");
        if (turn.Instruction is null
            || !turn.Instruction.Contains("longRunningShellId: 42", StringComparison.Ordinal)
            || !turn.Instruction.Contains(DysonLongRunningShellExitedFlow.TailSectionMarker, StringComparison.Ordinal)
            || !turn.Instruction.Contains("sample-error-token-xyz", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("BuildInstruction must include id + auto-read tail marker + sample output.");
        }

        DysonLongRunningShellExitedFlow.TrimInstructionAfterCompletion(turn);
        if (turn.Instruction is null
            || turn.Instruction.Contains(DysonLongRunningShellExitedFlow.TailSectionMarker, StringComparison.Ordinal)
            || turn.Instruction.Contains("sample-error-token-xyz", StringComparison.Ordinal)
            || !turn.Instruction.Contains("id: 42", StringComparison.Ordinal)
            || !turn.Instruction.Contains("outcome: failure", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TrimInstructionAfterCompletion must drop tail and keep id/outcome.");
        }
    }

    private static void AssertOutcomeMapping()
    {
        if (DysonLongRunningShellExitedFlow.MapOutcome(DysonLongRunningShellStatus.Aborted, exitCode: 1)
            != "cancelled")
        {
            throw new InvalidOperationException("Aborted must map to cancelled.");
        }

        if (DysonLongRunningShellExitedFlow.MapOutcome(
                DysonLongRunningShellStatus.Exited, exitCode: 0, wasCancelRequested: true)
            != "cancelled")
        {
            throw new InvalidOperationException("Soft-cancel then exit must map to cancelled.");
        }

        if (DysonLongRunningShellExitedFlow.MapOutcome(DysonLongRunningShellStatus.Exited, exitCode: 0)
            != "success")
        {
            throw new InvalidOperationException("Exited 0 must map to success.");
        }

        if (DysonLongRunningShellExitedFlow.MapOutcome(DysonLongRunningShellStatus.Exited, exitCode: 7)
            != "failure")
        {
            throw new InvalidOperationException("Exited non-zero must map to failure.");
        }

        if (DysonLongRunningShellExitedFlow.MapOutcome(DysonLongRunningShellStatus.Exited, exitCode: null)
            != "failure")
        {
            throw new InvalidOperationException("Exited unknown code must map to failure.");
        }
    }

    private static void AssertIdAllocationListAndAbort()
    {
        if (!OperatingSystem.IsWindows())
            return; // runners are Windows-only for now

        var workDirId = Guid.NewGuid();
        var cwd = Path.GetTempPath();

        try
        {
            // Short sleep so Abort has a live process.
            var a = DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "ping -n 30 127.0.0.1 >nul", cwd)
                .GetAwaiter()
                .GetResult();
            if (a.IsError)
                throw new InvalidOperationException($"Start #1 failed: {a.Error}");

            var b = DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "ping -n 30 127.0.0.1 >nul", cwd)
                .GetAwaiter()
                .GetResult();
            if (b.IsError)
                throw new InvalidOperationException($"Start #2 failed: {b.Error}");

            if (a.Value.Id != 1 || b.Value.Id != 2)
                throw new InvalidOperationException($"Expected incremental ids 1 then 2, got {a.Value.Id} then {b.Value.Id}.");

            if (a.Value.Status != DysonLongRunningShellStatus.Running
                || b.Value.Status != DysonLongRunningShellStatus.Running)
            {
                throw new InvalidOperationException("Started shells must be Running.");
            }

            if (DysonLongRunningShellRegistry.CountRunning(workDirId) != 2)
                throw new InvalidOperationException("CountRunning should be 2 after two starts.");

            var listed = DysonLongRunningShellRegistry.List(workDirId);
            if (listed.Count != 2 || listed[0].Id != 2 || listed[1].Id != 1)
                throw new InvalidOperationException("List after start must return both shells newest-id first.");

            var abort = DysonLongRunningShellRegistry
                .AbortAsync(workDirId, a.Value.Id, timeoutMs: 10_000)
                .GetAwaiter()
                .GetResult();
            if (abort.IsError)
                throw new InvalidOperationException($"Abort failed: {abort.Error}");

            if (!DysonLongRunningShellRegistry.TryGet(workDirId, a.Value.Id, out var shell) || shell is null)
                throw new InvalidOperationException("Aborted shell must remain in the registry list.");

            if (shell.Status is not (DysonLongRunningShellStatus.Aborted or DysonLongRunningShellStatus.Exited))
                throw new InvalidOperationException($"Abort must clear Running; got {shell.Status}.");

            if (DysonLongRunningShellRegistry.CountRunning(workDirId) != 1)
                throw new InvalidOperationException("CountRunning should be 1 after aborting one shell.");

            // Clean up the second process.
            _ = DysonLongRunningShellRegistry
                .AbortAsync(workDirId, b.Value.Id, timeoutMs: 10_000)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
        }
    }

    private static async Task AssertListAndCountFilterByWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workDirId = Guid.NewGuid();
        var dirA = Path.Combine(Path.GetTempPath(), "dyson-lrs-a-" + workDirId.ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "dyson-lrs-b-" + workDirId.ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        try
        {
            var a = DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "ping -n 30 127.0.0.1 >nul", dirA)
                .GetAwaiter()
                .GetResult();
            if (a.IsError)
                throw new InvalidOperationException($"Start A failed: {a.Error}");

            var b = DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "ping -n 30 127.0.0.1 >nul", dirB)
                .GetAwaiter()
                .GetResult();
            if (b.IsError)
                throw new InvalidOperationException($"Start B failed: {b.Error}");

            var unfiltered = DysonLongRunningShellRegistry.List(workDirId);
            if (unfiltered.Count != 2)
                throw new InvalidOperationException($"Unfiltered List should be 2, got {unfiltered.Count}.");
            if (DysonLongRunningShellRegistry.CountRunning(workDirId) != 2)
                throw new InvalidOperationException("Unfiltered CountRunning should be 2.");

            var filtered = DysonLongRunningShellRegistry.List(workDirId, dirA);
            if (filtered.Count != 1 || filtered[0].Id != a.Value.Id)
                throw new InvalidOperationException("Filtered List should return only the matching cwd shell.");
            if (DysonLongRunningShellRegistry.CountRunning(workDirId, dirA) != 1)
                throw new InvalidOperationException("Filtered CountRunning should be 1.");
            if (DysonLongRunningShellRegistry.CountRunning(workDirId, dirB) != 1)
                throw new InvalidOperationException("Filtered CountRunning for B should be 1.");

            var session = new StubSession();
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, dirA, http, store: null, workDirId);
            var listed = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "lrs-list-filter",
                ToolName = "ListLongRunningShells",
                Stage = 0,
                ArgumentsJson = "{}",
            }).GetAwaiter().GetResult();
            if (listed.IsError)
                throw new InvalidOperationException($"ListLongRunningShells failed: {listed.Content}");
            if (!listed.Content.Contains($"\"id\":{a.Value.Id}", StringComparison.Ordinal)
                || listed.Content.Contains($"\"id\":{b.Value.Id}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Executor ListLongRunningShells must be scoped to NativeRootPath. Got:\n" + listed.Content);
            }
        }
        finally
        {
            try
            {
                foreach (var info in DysonLongRunningShellRegistry.List(workDirId))
                    _ = DysonLongRunningShellRegistry.AbortAsync(workDirId, info.Id, timeoutMs: 10_000)
                        .GetAwaiter().GetResult();
            }
            catch
            {
                // best-effort
            }

            DysonLongRunningShellRegistry.ClearForTests(workDirId);
            try { Directory.Delete(dirA, recursive: true); } catch { /* ignore */ }
            try { Directory.Delete(dirB, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task AssertStartIncludesEarlyOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workDirId = Guid.NewGuid();
        var cwd = Path.Combine(Path.GetTempPath(), "dyson-lrs-start-tail-" + workDirId.ToString("N"));
        Directory.CreateDirectory(cwd);
        const string marker = "start-shell-tail-marker-xyz";

        try
        {
            var session = new StubSession();
            using var http = new HttpClient();
            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, cwd, http, store: null, workDirId);
            var call = new DysonToolCall
            {
                CallId = "lrs-start-tail",
                ToolName = "StartLongRunningShell",
                Stage = 0,
                ArgumentsJson = $$"""{"shell":"Cmd","command":"echo {{marker}}"}""",
            };

            var result = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException($"StartLongRunningShell failed: {result.Content}");

            var content = result.Content;
            if (!content.Contains("longRunningShellId=", StringComparison.Ordinal)
                || !content.Contains("status=", StringComparison.Ordinal)
                || !content.Contains("shell=", StringComparison.Ordinal)
                || !content.Contains("command=", StringComparison.Ordinal)
                || !content.Contains("---", StringComparison.Ordinal)
                || !content.Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "StartLongRunningShell result must include metadata and early output after ~1s. Got:\n" + content);
            }
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
            try
            {
                Directory.Delete(cwd, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    private static async Task AssertSubscribeRootOnly()
    {
        var workDirId = Guid.NewGuid();
        var cwd = Path.GetTempPath();
        var parent = new StubSession();
        var child = new StubSession();
        parent.RegisterForTest(child);

        var subscribe = DysonMcpPipeline.CreateLongRunningShellTools(["Cmd"])
            .First(t => t.Name == "SubscribeToLongRunningShellCompletion");
        child.McpPipeline.Tools[subscribe.Name] = subscribe;

        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(child, cwd, http, store: null, workDirId);
        var result = executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "sub-root",
            ToolName = "SubscribeToLongRunningShellCompletion",
            Stage = 0,
            ArgumentsJson = """{"longRunningShellId":1}""",
        }).GetAwaiter().GetResult();

        if (!result.IsError
            || !result.Content.Contains("root sessions only", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Child Subscribe must error with root sessions only. Got:\n" + result.Content);
        }
    }

    private static async Task AssertWaitForTimeoutValidation()
    {
        var workDirId = Guid.NewGuid();
        var cwd = Path.GetTempPath();
        var session = new StubSession();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, cwd, http, store: null, workDirId);
        try
        {
            foreach (var args in new[]
                     {
                         """{"longRunningShellId":1}""",
                         """{"longRunningShellId":1,"timeoutMs":0}""",
                         """{"longRunningShellId":1,"timeoutMs":-1}""",
                     })
            {
                var result = WaitForRaw(executor, args);
                if (!result.IsError
                    || !result.Content.Contains(
                        "WaitForLongRunningShellCompletion: timeoutMs (integer > 0) is required.",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Expected timeoutMs required for {args}. Got:\n" + result.Content);
                }
            }
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
        }
    }

    private static async Task AssertWaitForAlreadyExitedImmediate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workDirId = Guid.NewGuid();
        var cwd = Path.GetTempPath();
        var session = new StubSession();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, cwd, http, store: null, workDirId);
        try
        {
            var started = DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "echo wait-already-exited-marker", cwd)
                .GetAwaiter()
                .GetResult();
            if (started.IsError)
                throw new InvalidOperationException($"Start already-exited failed: {started.Error}");

            var waited = WaitFor(executor, started.Value.Id, timeoutMs: 10_000);
            if (waited.IsError)
                throw new InvalidOperationException($"WaitFor already-exited failed: {waited.Content}");
            AssertTerminalWaitJson(waited.Content);
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
        }
    }

    private static async Task AssertWaitForLiveTimeoutAndShortCommand()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workDirId = Guid.NewGuid();
        var cwd = Path.GetTempPath();
        var session = new StubSession();
        using var http = new HttpClient();
        var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, cwd, http, store: null, workDirId);
        try
        {
            var live = DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "ping -n 30 127.0.0.1 >nul", cwd)
                .GetAwaiter()
                .GetResult();
            if (live.IsError)
                throw new InvalidOperationException($"Start live failed: {live.Error}");

            var timedOut = WaitFor(executor, live.Value.Id, timeoutMs: 500);
            if (timedOut.IsError)
                throw new InvalidOperationException($"WaitFor timeout path failed: {timedOut.Content}");
            if (!timedOut.Content.Contains("\"status\":\"timeout\"", StringComparison.Ordinal)
                || !timedOut.Content.Contains("\"shellStatus\":\"Running\"", StringComparison.Ordinal)
                || timedOut.Content.Contains("\"outcome\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Timeout JSON must be status timeout, shellStatus Running, no outcome. Got:\n"
                    + timedOut.Content);
            }

            var abort = DysonLongRunningShellRegistry
                .AbortAsync(workDirId, live.Value.Id, timeoutMs: 10_000)
                .GetAwaiter()
                .GetResult();
            if (abort.IsError)
                throw new InvalidOperationException($"Abort live failed: {abort.Error}");

            var shortCmd = DysonLongRunningShellRegistry
                .StartAsync(workDirId, "Cmd", "cmd.exe", "echo wait-short-ok", cwd)
                .GetAwaiter()
                .GetResult();
            if (shortCmd.IsError)
                throw new InvalidOperationException($"Start short failed: {shortCmd.Error}");

            var done = WaitFor(executor, shortCmd.Value.Id, timeoutMs: 15_000);
            if (done.IsError)
                throw new InvalidOperationException($"WaitFor short failed: {done.Content}");
            AssertTerminalWaitJson(done.Content);

            if (session.TryDequeueInterrupt(out var interrupt))
            {
                throw new InvalidOperationException(
                    $"WaitFor must not enqueue interrupts (got {interrupt.Kind}).");
            }
        }
        finally
        {
            DysonLongRunningShellRegistry.ClearForTests(workDirId);
        }
    }

    private static DysonToolCallResult WaitFor(
        DysonWorkspaceToolExecutor executor,
        int id,
        int timeoutMs) =>
        WaitForRaw(executor, $$"""{"longRunningShellId":{{id}},"timeoutMs":{{timeoutMs}}}""");

    private static DysonToolCallResult WaitForRaw(DysonWorkspaceToolExecutor executor, string argumentsJson) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "wait-for",
            ToolName = "WaitForLongRunningShellCompletion",
            Stage = 0,
            ArgumentsJson = argumentsJson,
        }).GetAwaiter().GetResult();

    private static void AssertTerminalWaitJson(string content)
    {
        if (!content.Contains("\"status\":\"Exited\"", StringComparison.Ordinal)
            && !content.Contains("\"status\":\"Aborted\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected terminal status Exited/Aborted. Got:\n" + content);
        }

        if (!content.Contains("\"outcome\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Terminal WaitFor JSON must include outcome. Got:\n" + content);
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig { AvailableShells = [new DysonConfiguredShellSpec("Cmd", "cmd.exe")] },
        new StubProvider())
    {
        public void RegisterForTest(DysonAgentSession child) => RegisterSubagent(child);

        public void SetRuntimeIdForTest(int runtimeId) => Id = runtimeId;

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

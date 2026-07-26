using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only long-running shell id allocation + abort + list/subscribe/ShellExited (Xunit Fact).
/// /// </summary>
public class DysonLongRunningShellTests
{
    [Fact]
    public void Run()
    {
        AssertCatalogGate();
        AssertShellExitedPromptAndTrim();
        AssertOutcomeMapping();
        AssertIdAllocationListAndAbort();
    }

    private static void AssertCatalogGate()
    {
        var none = DysonMcpPipeline.CreateLongRunningShellTools([]).ToArray();
        if (none.Length != 0)
            throw new InvalidOperationException("Long-running shell tools must be omitted when no shells available.");

        var tools = DysonMcpPipeline.CreateLongRunningShellTools([DysonShellType.Pwsh], planMode: false).ToArray();
        if (tools.Length != 7)
            throw new InvalidOperationException($"Expected 7 long-running shell tools, got {tools.Length}.");

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
                 })
        {
            if (!names.Contains(required))
                throw new InvalidOperationException($"Missing long-running shell tool '{required}'.");
        }

        var start = tools.First(t => t.Name == "StartLongRunningShell");
        if (!start.Description.Contains("E2E", StringComparison.Ordinal)
            || !start.Description.Contains("ListLongRunningShells", StringComparison.Ordinal)
            || !start.Description.Contains("SubscribeToLongRunningShellCompletion", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("StartLongRunningShell description must mention E2E and List/Subscribe tools.");
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
                .StartAsync(workDirId, DysonShellType.Cmd, "ping -n 30 127.0.0.1 >nul", cwd)
                .GetAwaiter()
                .GetResult();
            if (a.IsError)
                throw new InvalidOperationException($"Start #1 failed: {a.Error}");

            var b = DysonLongRunningShellRegistry
                .StartAsync(workDirId, DysonShellType.Cmd, "ping -n 30 127.0.0.1 >nul", cwd)
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
            if (listed.Count != 2 || listed[0].Id != 1 || listed[1].Id != 2)
                throw new InvalidOperationException("List after start must return both shells in id order.");

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
}

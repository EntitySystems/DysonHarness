namespace DysonHarness;

/// <summary>
/// ponytail: assert-only long-running shell id allocation + abort (no test framework).
/// Run: <c>DysonLongRunningShellSelfCheck.Run()</c> (also from UI <c>Program</c> startup).
/// </summary>
public static class DysonLongRunningShellSelfCheck
{
    public static void Run()
    {
        AssertCatalogGate();
        AssertIdAllocationAndAbort();
    }

    private static void AssertCatalogGate()
    {
        var none = DysonMcpPipeline.CreateLongRunningShellTools([]).ToArray();
        if (none.Length != 0)
            throw new InvalidOperationException("Long-running shell tools must be omitted when no shells available.");

        var tools = DysonMcpPipeline.CreateLongRunningShellTools([DysonShellType.Pwsh], planMode: false).ToArray();
        if (tools.Length != 5)
            throw new InvalidOperationException($"Expected 5 long-running shell tools, got {tools.Length}.");

        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "StartLongRunningShell",
                     "ReadLongRunningShellTail",
                     "AbortLongRunningShell",
                     "RequestLongRunningShellCancellation",
                     "LongRunningShellInteract",
                 })
        {
            if (!names.Contains(required))
                throw new InvalidOperationException($"Missing long-running shell tool '{required}'.");
        }
    }

    private static void AssertIdAllocationAndAbort()
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

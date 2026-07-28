using DysonHarness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

/// <summary>ponytail: configured shell store seed / unique name / enabled-only list.</summary>
public class DysonConfiguredShellStoreTests
{
    [Fact]
    public void Run()
    {
        AssertSeedDefaultsAndEnabledList();
        AssertUniqueNameCaseInsensitive();
        AssertFixedArgsRoundTrip();
        AssertMcpEnumUsesEnabledNamesOnly();
    }

    private static void AssertSeedDefaultsAndEnabledList()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = new DysonConfiguredShellStore(accessor);

        var ensure = store.EnsureDefaultsAsync().GetAwaiter().GetResult();
        if (ensure.IsError)
            throw new InvalidOperationException(ensure.Error);

        var again = store.EnsureDefaultsAsync().GetAwaiter().GetResult();
        if (again.IsError)
            throw new InvalidOperationException(again.Error);

        var list = store.ListAsync().GetAwaiter().GetResult();
        if (list.IsError)
            throw new InvalidOperationException(list.Error);
        if (list.Value.Count != 3)
            throw new InvalidOperationException($"Expected 3 seeded shells, got {list.Value.Count}.");

        var names = list.Value.Select(s => s.Name).ToArray();
        if (names is not ["Pwsh", "PowerShell", "Cmd"])
            throw new InvalidOperationException("Seed names/order mismatch: " + string.Join(", ", names));

        var disable = store.UpdateAsync(list.Value[0].Id, "Pwsh", "pwsh", isEnabled: false)
            .GetAwaiter().GetResult();
        if (disable.IsError)
            throw new InvalidOperationException(disable.Error);

        var enabled = store.ListEnabledSpecsAsync().GetAwaiter().GetResult();
        if (enabled.IsError)
            throw new InvalidOperationException(enabled.Error);
        if (enabled.Value.Count != 2
            || enabled.Value.Any(s => s.Name == "Pwsh"))
        {
            throw new InvalidOperationException("ListEnabledSpecsAsync must omit disabled shells.");
        }
    }

    private static void AssertUniqueNameCaseInsensitive()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = new DysonConfiguredShellStore(accessor);

        var a = store.CreateAsync("MyShell", "cmd.exe").GetAwaiter().GetResult();
        if (a.IsError)
            throw new InvalidOperationException(a.Error);

        var dup = store.CreateAsync("myshell", "cmd.exe").GetAwaiter().GetResult();
        if (!dup.IsError)
            throw new InvalidOperationException("Duplicate CI name must be rejected.");
    }

    private static void AssertFixedArgsRoundTrip()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = new DysonConfiguredShellStore(accessor);

        var created = store.CreateAsync("GitBash", @"C:\Git\bin\bash.exe", fixedArgs: ["-c"])
            .GetAwaiter().GetResult();
        if (created.IsError)
            throw new InvalidOperationException(created.Error);

        var list = store.ListAsync().GetAwaiter().GetResult();
        if (list.IsError)
            throw new InvalidOperationException(list.Error);

        var row = list.Value.Single(s => s.Id == created.Value);
        if (row.FixedArgsJson != """["-c"]""")
            throw new InvalidOperationException($"FixedArgsJson mismatch: {row.FixedArgsJson}");

        var specs = store.ListEnabledSpecsAsync().GetAwaiter().GetResult();
        if (specs.IsError)
            throw new InvalidOperationException(specs.Error);

        var spec = specs.Value.Single(s => s.Name == "GitBash");
        if (spec.FixedArgs is not ["-c"])
            throw new InvalidOperationException("ListEnabledSpecsAsync must surface FixedArgs.");

        var cleared = store.UpdateAsync(created.Value, "GitBash", @"C:\Git\bin\bash.exe", isEnabled: true, fixedArgs: null)
            .GetAwaiter().GetResult();
        if (cleared.IsError)
            throw new InvalidOperationException(cleared.Error);

        var after = store.ListEnabledSpecsAsync().GetAwaiter().GetResult();
        if (after.IsError)
            throw new InvalidOperationException(after.Error);
        if (after.Value.Single(s => s.Name == "GitBash").FixedArgs is not null)
            throw new InvalidOperationException("Clearing FixedArgs must yield null on specs.");

        if (DysonConfiguredShellStore.ParseFixedArgsText("-NoProfile -Command") is not ["-NoProfile", "-Command"])
            throw new InvalidOperationException("ParseFixedArgsText must split on whitespace.");
    }

    private static void AssertMcpEnumUsesEnabledNamesOnly()
    {
        var config = new DysonAgentSessionConfig
        {
            AvailableShells =
            [
                new DysonConfiguredShellSpec("Cmd", "cmd.exe"),
                new DysonConfiguredShellSpec("MyPwsh", @"C:\Tools\pwsh.exe"),
            ],
        };

        var pipeline = DysonSessionToolsetBuilder.Build(config, DysonAgentModes.Work);
        if (!pipeline.Tools.TryGetValue("ShellExecute", out var tool))
            throw new InvalidOperationException("ShellExecute must be present when shells are configured.");

        if (!tool.InputSchemaJson.Contains("\"Cmd\"", StringComparison.Ordinal)
            || !tool.InputSchemaJson.Contains("\"MyPwsh\"", StringComparison.Ordinal)
            || tool.InputSchemaJson.Contains("\"Pwsh\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ShellExecute enum must list only session AvailableShells names. Schema:\n" + tool.InputSchemaJson);
        }

        var empty = DysonSessionToolsetBuilder.Build(
            new DysonAgentSessionConfig { AvailableShells = [] },
            DysonAgentModes.Work);
        if (empty.Tools.ContainsKey("ShellExecute")
            || empty.Tools.ContainsKey("StartLongRunningShell")
            || empty.Tools.ContainsKey("ListLongRunningShells"))
        {
            throw new InvalidOperationException("Empty AvailableShells must omit ShellExecute and all LRS tools.");
        }
    }

}

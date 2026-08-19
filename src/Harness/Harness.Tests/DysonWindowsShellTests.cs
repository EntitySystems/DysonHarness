using DysonHarness;

namespace Harness.Tests;

/// <summary>Windows shell arg map stays as documented (Xunit).</summary>
public class DysonWindowsShellTests
{
    [Fact]
    public void ArgMap_MatchesDocumentedValues()
    {
        var pwsh = DysonWindowsShell.MapArgs(DysonShellType.Pwsh);
        if (pwsh.FileName != "pwsh"
            || pwsh.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("Pwsh arg map mismatch.");
        }

        var ps = DysonWindowsShell.MapArgs(DysonShellType.PowerShell);
        if (ps.FileName != "powershell.exe"
            || ps.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("PowerShell arg map mismatch.");
        }

        var cmd = DysonWindowsShell.MapArgs(DysonShellType.Cmd);
        if (cmd.FileName != "cmd.exe" || cmd.FixedArgs is not ["/d", "/c"])
            throw new InvalidOperationException("Cmd arg map mismatch.");
    }

    [Fact]
    public void BasenameHeuristics_UseExecutablePathAsFileName()
    {
        var customPwsh = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Tools\pwsh.exe");
        if (customPwsh.IsError
            || customPwsh.Value.FileName != @"C:\Tools\pwsh.exe"
            || customPwsh.Value.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("Basename heuristics must keep full path and Pwsh fixed args.");
        }

        var bash = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Program Files\Git\bin\bash.exe");
        if (bash.IsError
            || bash.Value.FileName != @"C:\Program Files\Git\bin\bash.exe"
            || bash.Value.FixedArgs is not ["-c"])
        {
            throw new InvalidOperationException("bash basename must map to -c.");
        }

        var gitBash = DysonWindowsShell.MapFixedArgsFromExecutablePath("git-bash.exe");
        if (gitBash.IsError || gitBash.Value.FixedArgs is not ["-c"])
            throw new InvalidOperationException("git-bash basename must map to -c.");

        var python = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Python311\python.exe");
        if (python.IsError
            || python.Value.FileName != @"C:\Python311\python.exe"
            || python.Value.FixedArgs is not ["-c"])
        {
            throw new InvalidOperationException("python basename must keep full path and map to -c.");
        }

        var python3 = DysonWindowsShell.MapFixedArgsFromExecutablePath("python3");
        if (python3.IsError || python3.Value.FixedArgs is not ["-c"])
            throw new InvalidOperationException("python3 basename must map to -c.");

        var node = DysonWindowsShell.MapFixedArgsFromExecutablePath(@"C:\Program Files\nodejs\node.exe");
        if (node.IsError
            || node.Value.FileName != @"C:\Program Files\nodejs\node.exe"
            || node.Value.FixedArgs is not ["-e"])
        {
            throw new InvalidOperationException("node basename must keep full path and map to -e.");
        }

        var nodejs = DysonWindowsShell.MapFixedArgsFromExecutablePath("nodejs.exe");
        if (nodejs.IsError || nodejs.Value.FixedArgs is not ["-e"])
            throw new InvalidOperationException("nodejs basename must map to -e.");

        var unknown = DysonWindowsShell.MapFixedArgsFromExecutablePath("foo.exe");
        if (!unknown.IsError
            || !unknown.Error.Contains("Fixed args", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Unsupported basenames must fail and mention Settings Fixed args.");
        }
    }

    [Fact]
    public void ResolveFixedArgs_OverrideWinsOverBasename()
    {
        var overridden = DysonWindowsShell.ResolveFixedArgs("python.exe", ["-c"]);
        if (overridden.IsError
            || overridden.Value.FileName != "python.exe"
            || overridden.Value.FixedArgs is not ["-c"])
        {
            throw new InvalidOperationException("Non-empty FixedArgs override must skip basename heuristics.");
        }

        var emptyOverride = DysonWindowsShell.ResolveFixedArgs(@"C:\Tools\bash.exe", []);
        if (emptyOverride.IsError || emptyOverride.Value.FixedArgs is not ["-c"])
            throw new InvalidOperationException("Empty override must fall back to basename heuristics.");

        var nullOverride = DysonWindowsShell.ResolveFixedArgs("pwsh", null);
        if (nullOverride.IsError
            || nullOverride.Value.FixedArgs is not ["-NoProfile", "-NonInteractive", "-Command"])
        {
            throw new InvalidOperationException("Null override must use basename heuristics.");
        }
    }
}

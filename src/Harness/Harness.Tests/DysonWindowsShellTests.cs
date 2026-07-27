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

        var unknown = DysonWindowsShell.MapFixedArgsFromExecutablePath("python.exe");
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

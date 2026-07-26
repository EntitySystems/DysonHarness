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
}

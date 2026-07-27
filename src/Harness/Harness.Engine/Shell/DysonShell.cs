namespace DysonHarness;

/// <summary>
/// Thin shell runner. Public <see cref="ShellType"/> identifies the concrete runner when created by type.
/// </summary>
public abstract class DysonShell
{
    public abstract DysonShellType ShellType { get; }

    public abstract Task<Result<DysonShellRunResult, string>> ExecuteAsync(
        string command,
        string workingDirectory,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    public static DysonShell Create(DysonShellType type) => type switch
    {
        DysonShellType.Pwsh or DysonShellType.PowerShell or DysonShellType.Cmd
            => new DysonWindowsShell(type),
        // ponytail: Bash/Zsh runners when macOS/Linux availability lands
        _ => throw new NotSupportedException($"Shell '{type}' is not implemented yet."),
    };

    /// <summary>
    /// Default shell display names for the current OS (Settings catalog / CreateDefault fallback).
    /// Windows: Pwsh, PowerShell, Cmd. Other platforms: none yet.
    /// </summary>
    public static IReadOnlyList<string> DefaultShellNamesForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return ["Pwsh", "PowerShell", "Cmd"];

        // ponytail: return Bash/Zsh for macOS/Linux later
        return [];
    }

    /// <summary>Default type list for legacy callers / tests that still use <see cref="DysonShellType"/>.</summary>
    public static IReadOnlyList<DysonShellType> AvailableForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return [DysonShellType.Pwsh, DysonShellType.PowerShell, DysonShellType.Cmd];

        return [];
    }

    /// <summary>Default session specs matching Windows seed paths (tests / catalog fallback).</summary>
    public static IReadOnlyList<DysonConfiguredShellSpec> DefaultConfiguredShellsForCurrentPlatform()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        return
        [
            new DysonConfiguredShellSpec("Pwsh", "pwsh"),
            new DysonConfiguredShellSpec("PowerShell", "powershell.exe"),
            new DysonConfiguredShellSpec("Cmd", "cmd.exe"),
        ];
    }
}

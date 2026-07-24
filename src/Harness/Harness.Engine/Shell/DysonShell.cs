namespace DysonHarness;

/// <summary>
/// Thin shell runner. Public <see cref="ShellType"/> identifies the concrete runner.
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
    /// Shells offered to the model for the current OS.
    /// Windows: Pwsh, PowerShell, Cmd. Other platforms: none yet.
    /// </summary>
    public static IReadOnlyList<DysonShellType> AvailableForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return [DysonShellType.Pwsh, DysonShellType.PowerShell, DysonShellType.Cmd];

        // ponytail: return Bash/Zsh (and runners) for macOS/Linux later
        return [];
    }
}

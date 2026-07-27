namespace DysonHarness;

/// <summary>User-configured shell (name + executable) for MCP ShellExecute / long-running shells.</summary>
public sealed class DysonConfiguredShellEntity
{
    public Guid Id { get; set; }

    /// <summary>MCP enum value; unique case-insensitive.</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute path or PATH-resolvable file name.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// Optional JSON string array of argv prefix before the command (e.g. <c>["-c"]</c>).
    /// Null/empty ⇒ basename heuristics in <see cref="DysonWindowsShell.ResolveFixedArgs"/>.
    /// </summary>
    public string? FixedArgsJson { get; set; }

    /// <summary>When false, omitted from session MCP catalogs.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Stable UI / MCP enum order (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }
}

namespace DysonHarness;

/// <summary>Enabled shell available to a session (MCP name → executable path + optional fixed args).</summary>
public sealed record DysonConfiguredShellSpec(
    string Name,
    string ExecutablePath,
    IReadOnlyList<string>? FixedArgs = null);

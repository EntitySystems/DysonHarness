namespace DysonHarness;

public enum DysonPluginInstallScope
{
    Project = 0,
    Global = 1,
}

public enum DysonPluginSourceKind
{
    LocalZip = 0,
    LocalFolder = 1,
    GitHub = 2,
}

public enum DysonPluginPackageFormat
{
    AgentPlugin = 0,
    Codex = 1,
    Cursor = 2,
}

public enum DysonPluginComponentKind
{
    Skill = 0,
    McpServer = 1,
    Rule = 2,
    Agent = 3,
    Command = 4,
    Hook = 5,
    Variable = 6,
    Unsupported = 7,
}

public enum DysonPluginDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public enum DysonPluginStatus
{
    Previewed = 0,
    Installed = 1,
    Disabled = 2,
    Invalid = 3,
    UpdateAvailable = 4,
}

[Flags]
public enum DysonPluginCapabilities
{
    None = 0,
    Skills = 1 << 0,
    Instructions = 1 << 1,
    McpNetwork = 1 << 2,
    McpExecutable = 1 << 3,
    Hooks = 1 << 4,
    Variables = 1 << 5,
    UnsupportedComponents = 1 << 6,
}

public sealed record DysonPluginSource
{
    public required DysonPluginSourceKind Kind { get; init; }
    public required string Location { get; init; }
    public string? RequestedRef { get; init; }
    public string? ResolvedCommit { get; init; }
    public string? Subdirectory { get; init; }
    public string? ContentChecksum { get; init; }
}

public sealed record DysonPluginManifestMetadata
{
    public required string NormalizedId { get; init; }
    public required string DisplayName { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? SchemaVersion { get; init; }
}

public sealed record DysonResolvedPluginComponent
{
    public required string Id { get; init; }
    public required DysonPluginComponentKind Kind { get; init; }
    public required string RelativePath { get; init; }
    public bool IsSupported { get; init; } = true;
    public bool EnabledByDefault { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record DysonPluginDiagnostic
{
    public required DysonPluginDiagnosticSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? ComponentId { get; init; }
}

public sealed record DysonResolvedPlugin
{
    public required DysonPluginPackageFormat Format { get; init; }
    public required DysonPluginManifestMetadata Manifest { get; init; }
    public required DysonPluginSource Source { get; init; }
    public DysonPluginCapabilities Capabilities { get; init; }
    public IReadOnlyList<DysonResolvedPluginComponent> Components { get; init; } = [];
    public IReadOnlyList<DysonPluginDiagnostic> Diagnostics { get; init; } = [];
    public string? ConfigurationSchemaJson { get; init; }
}

public sealed record DysonPluginPreview
{
    public required Guid PreviewId { get; init; }
    public required DysonResolvedPlugin Plugin { get; init; }
    public required string StagedPackageRoot { get; init; }
    public required DateTime CreatedUtc { get; init; }
}

public sealed record DysonPluginInstallResult
{
    public required Guid InstallationId { get; init; }
    public required DysonResolvedPlugin Plugin { get; init; }
    public required DysonPluginInstallScope Scope { get; init; }
    public Guid? WorkDirectoryId { get; init; }
    public required string PackageRoot { get; init; }
    public required string PluginDataRoot { get; init; }
    public required DateTime InstalledUtc { get; init; }
}

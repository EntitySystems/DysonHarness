namespace DysonHarness;

/// <summary>
/// Durable subject-owned plugin installation record. String discriminators preserve storage
/// independence from Engine package/runtime models.
/// </summary>
public sealed class DysonPluginInstallationEntity
{
    public Guid Id { get; set; }
    public string SubjectId { get; set; } = "";
    public string NormalizedPluginId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Version { get; set; }
    public string SourceKind { get; set; } = "";
    public string SourceLocation { get; set; } = "";
    public string? RequestedRef { get; set; }
    public string? SourceSubdirectory { get; set; }
    public string? ResolvedCommit { get; set; }
    public string? ContentChecksum { get; set; }
    public string PackageFormat { get; set; } = "";
    public string? SchemaVersion { get; set; }
    public string InstallScope { get; set; } = "";
    public Guid? WorkDirectoryId { get; set; }
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = "";
    public string PackageRoot { get; set; } = "";
    public string ComponentInventoryJson { get; set; } = "[]";
    public string? ConfigurationSchemaJson { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public DateTime InstalledUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public DysonWorkDirectoryEntity? WorkDirectory { get; set; }
}

public static class DysonPluginStorageValues
{
    public const string ProjectScope = "Project";
    public const string GlobalScope = "Global";

    public static readonly IReadOnlySet<string> SourceKinds =
        new HashSet<string>(["LocalZip", "LocalFolder", "GitHub"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> PackageFormats =
        new HashSet<string>(["AgentPlugin", "Codex", "Cursor"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Statuses =
        new HashSet<string>(["Previewed", "Installed", "Disabled", "Invalid", "UpdateAvailable"], StringComparer.Ordinal);
}

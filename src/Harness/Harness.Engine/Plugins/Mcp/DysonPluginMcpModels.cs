namespace DysonHarness;

public enum DysonPluginMcpTransportKind
{
    Unknown = 0,
    Stdio = 1,
    StreamableHttp = 2,
    Sse = 3,
}

[Flags]
public enum DysonPluginMcpRuntimeCapability
{
    None = 0,
    Executable = 1 << 0,
    Network = 1 << 1,
}

public enum DysonPluginMcpServerState
{
    Denied = 0,
    Unavailable = 1,
    Disconnected = 2,
    Connected = 3,
    Error = 4,
}

/// <summary>
/// Explicit runtime approval for one installed plugin server. Installation enablement is not a
/// runtime approval. Capability bits prevent a prior network approval from silently authorizing a
/// later executable declaration (or the reverse) after a package update.
/// </summary>
public sealed record DysonPluginMcpRuntimeGrant
{
    public required Guid InstallationId { get; init; }
    public required string ServerId { get; init; }
    public required DysonPluginMcpRuntimeCapability Capabilities { get; init; }
}

/// <summary>Default-deny activation input supplied by a future reviewed permissions UI.</summary>
public sealed record DysonPluginMcpRuntimeActivation
{
    public static DysonPluginMcpRuntimeActivation DenyAll { get; } = new();

    public IReadOnlyList<DysonPluginMcpRuntimeGrant> Grants { get; init; } = [];

    public VoidResult<string> Validate()
    {
        var keys = new HashSet<(Guid InstallationId, string ServerId)>();
        foreach (var grant in Grants)
        {
            if (grant.InstallationId == Guid.Empty)
                return VoidResult<string>.AsError("Plugin MCP grants require an installation id.");
            if (string.IsNullOrWhiteSpace(grant.ServerId))
                return VoidResult<string>.AsError("Plugin MCP grants require a server id.");
            if (grant.Capabilities == DysonPluginMcpRuntimeCapability.None ||
                (grant.Capabilities & ~(DysonPluginMcpRuntimeCapability.Executable | DysonPluginMcpRuntimeCapability.Network)) != 0)
            {
                return VoidResult<string>.AsError(
                    $"Plugin MCP grant for '{grant.ServerId}' has unsupported capabilities.");
            }
            if (!keys.Add((grant.InstallationId, grant.ServerId.Trim())))
            {
                return VoidResult<string>.AsError(
                    $"Duplicate plugin MCP grant for installation '{grant.InstallationId}', server '{grant.ServerId.Trim()}'.");
            }
        }

        return VoidResult<string>.Success;
    }

    public bool IsGranted(Guid installationId, string serverId, DysonPluginMcpTransportKind transport)
    {
        var required = transport == DysonPluginMcpTransportKind.Stdio
            ? DysonPluginMcpRuntimeCapability.Executable
            : DysonPluginMcpRuntimeCapability.Network;
        return Grants.Any(grant =>
            grant.InstallationId == installationId &&
            string.Equals(grant.ServerId.Trim(), serverId, StringComparison.Ordinal) &&
            (grant.Capabilities & required) == required);
    }
}

public sealed record DysonPluginMcpServerDeclaration
{
    public required Guid InstallationId { get; init; }
    public required string PluginId { get; init; }
    public required string ServerId { get; init; }
    public required string ComponentRelativePath { get; init; }
    public required string PackageRoot { get; init; }
    public required string PluginDataRoot { get; init; }
    public required DysonPluginMcpTransportKind Transport { get; init; }
    public bool IsAvailable { get; init; }
    public string? UnavailableReason { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string? Cwd { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record DysonPluginMcpResolvedCatalog
{
    public IReadOnlyList<DysonPluginMcpServerDeclaration> Servers { get; init; } = [];
    public IReadOnlyList<DysonPluginDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record DysonPluginMcpToolMetadata
{
    public required string CatalogName { get; init; }
    public required Guid InstallationId { get; init; }
    public required string PluginId { get; init; }
    public required string ServerId { get; init; }
    public required string RemoteToolName { get; init; }
    public required DysonPluginMcpTransportKind Transport { get; init; }
    public required DysonMcpTool Tool { get; init; }
}

public sealed record DysonPluginMcpServerStatus
{
    public required Guid InstallationId { get; init; }
    public required string PluginId { get; init; }
    public required string ServerId { get; init; }
    public required DysonPluginMcpTransportKind Transport { get; init; }
    public required DysonPluginMcpServerState State { get; init; }
    public bool RuntimeGranted { get; init; }
    public int ToolCount { get; init; }
    public string? LastError { get; init; }
}

public sealed record DysonPluginMcpHostSnapshot
{
    public IReadOnlyList<DysonPluginMcpServerStatus> Servers { get; init; } = [];
    public IReadOnlyList<DysonPluginMcpToolMetadata> Tools { get; init; } = [];
    public IReadOnlyList<DysonPluginDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record DysonPluginMcpRemoteTool
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string InputSchemaJson { get; init; } = """{"type":"object","properties":{}}""";
}

/// <summary>Small testable runtime seam; the production connector wraps the existing MCP SDK client.</summary>
public interface IDysonPluginMcpConnection : IAsyncDisposable
{
    Task<Result<IReadOnlyList<DysonPluginMcpRemoteTool>, string>> ListToolsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<string, string>> CallToolAsync(
        string remoteToolName,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}

public interface IDysonPluginMcpConnector
{
    Task<Result<IDysonPluginMcpConnection, string>> ConnectAsync(
        DysonPluginMcpServerDeclaration declaration,
        CancellationToken cancellationToken = default);
}

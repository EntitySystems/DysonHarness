namespace DysonHarness;

/// <summary>Encrypted, subject-owned plugin variable value. Plaintext is never persisted.</summary>
public sealed class DysonPluginVariableValueEntity
{
    public Guid Id { get; set; }
    public string SubjectId { get; set; } = "";
    public Guid InstallationId { get; set; }
    public string VariableName { get; set; } = "";
    public byte[] ProtectedValue { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DysonPluginInstallationEntity? Installation { get; set; }
}

/// <summary>
/// Explicit subject-owned runtime grant for one managed plugin MCP server. The package checksum
/// binds approval to the exact installed payload; updates therefore make prior grants stale.
/// </summary>
public sealed class DysonPluginMcpGrantEntity
{
    public Guid Id { get; set; }
    public string SubjectId { get; set; } = "";
    public Guid InstallationId { get; set; }
    public string ServerId { get; set; } = "";
    public int Capabilities { get; set; }
    public string PackageChecksum { get; set; } = "";
    public DateTime GrantedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public DysonPluginInstallationEntity? Installation { get; set; }
}

/// <summary>Durable review grant for one installed hook component and supported Dyson event.</summary>
public sealed class DysonPluginHookReviewEntity
{
    public Guid Id { get; set; }
    public string SubjectId { get; set; } = "";
    public Guid InstallationId { get; set; }
    public string HookComponentId { get; set; } = "";
    public string EventName { get; set; } = "";
    public string PermissionsJson { get; set; } = "[]";
    public string FailureMode { get; set; } = "";
    public int TimeoutMilliseconds { get; set; }
    public int MaxOutputBytes { get; set; }
    public string? PackageChecksum { get; set; }
    public DateTime ReviewedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public DysonPluginInstallationEntity? Installation { get; set; }
}

/// <summary>Append-only, subject-owned hook audit row containing bounded metadata only.</summary>
public sealed class DysonPluginHookAuditEntity
{
    public Guid Id { get; set; }
    public string SubjectId { get; set; } = "";
    public Guid InstallationId { get; set; }
    public string HookComponentId { get; set; } = "";
    public string EventName { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string DetailCode { get; set; } = "";
    public int DurationMilliseconds { get; set; }
    public int InputBytes { get; set; }
    public int OutputBytes { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DysonPluginInstallationEntity? Installation { get; set; }
}

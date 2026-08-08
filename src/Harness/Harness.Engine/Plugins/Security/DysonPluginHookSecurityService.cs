using System.Text.Json;

namespace DysonHarness;

public enum DysonPluginHookFailureMode { FailOpen = 0, FailClosed = 1 }

public static class DysonPluginHookEvents
{
    public const string ContextPrepared = "context.prepared";
    public const string ToolBefore = "tool.before";
    public const string ToolAfter = "tool.after";
    public const string McpBefore = "mcp.before";
    public const string McpAfter = "mcp.after";
    public const string ShellBefore = "shell.before";
    public const string ShellAfter = "shell.after";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(
        [ContextPrepared, ToolBefore, ToolAfter, McpBefore, McpAfter, ShellBefore, ShellAfter], StringComparer.Ordinal);
}

public static class DysonPluginHookPermissions
{
    public const string ReadContextMetadata = "context.metadata.read";
    public const string ReadToolMetadata = "tool.metadata.read";
    public const string GateTool = "tool.gate";
    public const string ReadMcpMetadata = "mcp.metadata.read";
    public const string ReadShellMetadata = "shell.metadata.read";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(
        [ReadContextMetadata, ReadToolMetadata, GateTool, ReadMcpMetadata, ReadShellMetadata], StringComparer.Ordinal);
}

public sealed record DysonPluginHookReviewGrant
{
    public required Guid InstallationId { get; init; }
    public required string HookComponentId { get; init; }
    public required string EventName { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
    public DysonPluginHookFailureMode FailureMode { get; init; }
    public int TimeoutMilliseconds { get; init; }
    public int MaxOutputBytes { get; init; }
    public required DateTime ReviewedUtc { get; init; }
}

public sealed record DysonPluginHookReviewStatus
{
    public bool IsGranted { get; init; }
    public DysonPluginHookReviewGrant? Grant { get; init; }
    public string DenialReason { get; init; } = "No active review grant.";
}

public sealed record DysonPluginHookAuditWrite
{
    public required Guid InstallationId { get; init; }
    public required string HookComponentId { get; init; }
    public required string EventName { get; init; }
    public required string Outcome { get; init; }
    public string? DetailCode { get; init; }
    public int DurationMilliseconds { get; init; }
    public int InputBytes { get; init; }
    public int OutputBytes { get; init; }
}

public sealed class DysonPluginHookSecurityService(
    IDysonPluginInstallationRepository installations,
    IDysonPluginHookSecurityRepository repository)
{
    public const int MinTimeoutMilliseconds = 50;
    public const int MaxTimeoutMilliseconds = 30_000;
    public const int MinOutputBytes = 256;
    public const int MaxOutputBytes = 1_048_576;

    private readonly IDysonPluginInstallationRepository _installations = installations ?? throw new ArgumentNullException(nameof(installations));
    private readonly IDysonPluginHookSecurityRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    private static readonly IReadOnlySet<string> AuditOutcomes = new HashSet<string>(
        ["allowed", "denied", "failed", "skipped", "timeout"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> AuditDetailCodes = new HashSet<string>(
        [
            "none", "completed", "default-denied", "review-denied", "invalid-output", "timeout", "revoked",
            "stale-review", "unsupported-event", "resolution-failed", "process-failed", "output-limit", "stderr-limit",
        ],
        StringComparer.Ordinal);

    public async Task<VoidResult<string>> GrantAsync(DysonPluginHookReviewGrant grant, CancellationToken cancellationToken = default)
    {
        if (grant is null) return VoidResult<string>.AsError("Plugin hook review grant is required.");
        if (!DysonPluginHookEvents.Supported.Contains(grant.EventName)) return VoidResult<string>.AsError("Unsupported plugin hook event cannot be granted.");
        if (grant.Permissions.Count == 0 || grant.Permissions.Any(x => !DysonPluginHookPermissions.Supported.Contains(x)))
            return VoidResult<string>.AsError("Plugin hook permissions contain an unsupported value.");
        if (grant.Permissions.Distinct(StringComparer.Ordinal).Count() != grant.Permissions.Count)
            return VoidResult<string>.AsError("Plugin hook permissions must be unique.");
        if (grant.TimeoutMilliseconds is < MinTimeoutMilliseconds or > MaxTimeoutMilliseconds)
            return VoidResult<string>.AsError($"Plugin hook timeout must be between {MinTimeoutMilliseconds} and {MaxTimeoutMilliseconds} milliseconds.");
        if (grant.MaxOutputBytes is < MinOutputBytes or > MaxOutputBytes)
            return VoidResult<string>.AsError($"Plugin hook output limit must be between {MinOutputBytes} and {MaxOutputBytes} bytes.");
        if (grant.ReviewedUtc == default)
            return VoidResult<string>.AsError("Plugin hook reviewer timestamp is required.");
        var installation = await _installations.GetAsync(grant.InstallationId, cancellationToken).ConfigureAwait(false);
        if (installation.IsError) return VoidResult<string>.AsError(installation.Error);
        if (!HasHookComponent(installation.Value.ComponentInventoryJson, grant.HookComponentId))
            return VoidResult<string>.AsError("Plugin hook component is not declared by this installation.");
        return await _repository.UpsertReviewAsync(new DysonPluginHookReviewEntity
        {
            InstallationId = grant.InstallationId,
            HookComponentId = grant.HookComponentId,
            EventName = grant.EventName,
            PermissionsJson = JsonSerializer.Serialize(grant.Permissions.OrderBy(x => x, StringComparer.Ordinal)),
            FailureMode = grant.FailureMode.ToString(),
            TimeoutMilliseconds = grant.TimeoutMilliseconds,
            MaxOutputBytes = grant.MaxOutputBytes,
            PackageChecksum = installation.Value.ContentChecksum,
            ReviewedUtc = grant.ReviewedUtc.Kind == DateTimeKind.Utc ? grant.ReviewedUtc : grant.ReviewedUtc.ToUniversalTime(),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DysonPluginHookReviewStatus, string>> GetStatusAsync(Guid installationId, string hookComponentId, string eventName, CancellationToken cancellationToken = default)
    {
        if (!DysonPluginHookEvents.Supported.Contains(eventName))
            return Result<DysonPluginHookReviewStatus, string>.AsValue(new DysonPluginHookReviewStatus { DenialReason = "Unsupported plugin hook event." });
        var installation = await _installations.GetAsync(installationId, cancellationToken).ConfigureAwait(false);
        if (installation.IsError) return Result<DysonPluginHookReviewStatus, string>.AsError(installation.Error);
        if (!installation.Value.IsEnabled ||
            installation.Value.Status is not ("Installed" or "UpdateAvailable") ||
            !HasHookComponent(installation.Value.ComponentInventoryJson, hookComponentId))
            return Result<DysonPluginHookReviewStatus, string>.AsValue(new DysonPluginHookReviewStatus { DenialReason = "Plugin hook is dormant." });
        var stored = await _repository.GetReviewAsync(installationId, hookComponentId, eventName, cancellationToken).ConfigureAwait(false);
        if (stored.IsError) return Result<DysonPluginHookReviewStatus, string>.AsError(stored.Error);
        var row = stored.Value;
        if (row is null)
            return Result<DysonPluginHookReviewStatus, string>.AsValue(new DysonPluginHookReviewStatus());
        if (row.RevokedUtc is not null)
        {
            return Result<DysonPluginHookReviewStatus, string>.AsValue(new DysonPluginHookReviewStatus
            {
                DenialReason = "Plugin hook review was revoked.",
            });
        }
        if (!string.Equals(row.PackageChecksum, installation.Value.ContentChecksum, StringComparison.Ordinal))
            return Result<DysonPluginHookReviewStatus, string>.AsValue(new DysonPluginHookReviewStatus { DenialReason = "Plugin hook review is stale after package change." });
        try
        {
            var permissions = JsonSerializer.Deserialize<string[]>(row.PermissionsJson) ?? [];
            if (!Enum.TryParse<DysonPluginHookFailureMode>(row.FailureMode, out var failureMode))
                return Result<DysonPluginHookReviewStatus, string>.AsError("Stored plugin hook review is invalid.");
            return Result<DysonPluginHookReviewStatus, string>.AsValue(new DysonPluginHookReviewStatus
            {
                IsGranted = true,
                DenialReason = "",
                Grant = new DysonPluginHookReviewGrant
                {
                    InstallationId = installationId, HookComponentId = hookComponentId, EventName = eventName,
                    Permissions = permissions, FailureMode = failureMode, TimeoutMilliseconds = row.TimeoutMilliseconds,
                    MaxOutputBytes = row.MaxOutputBytes, ReviewedUtc = row.ReviewedUtc,
                },
            });
        }
        catch (JsonException)
        {
            return Result<DysonPluginHookReviewStatus, string>.AsError("Stored plugin hook review is invalid.");
        }
    }

    public Task<VoidResult<string>> RevokeAsync(Guid installationId, string hookComponentId, string eventName, CancellationToken cancellationToken = default) =>
        _repository.RevokeReviewAsync(installationId, hookComponentId, eventName, DateTime.UtcNow, cancellationToken);

    public async Task<VoidResult<string>> AppendAuditAsync(DysonPluginHookAuditWrite audit, CancellationToken cancellationToken = default)
    {
        if (audit is null) return VoidResult<string>.AsError("Plugin hook audit record is required.");
        if (!DysonPluginHookEvents.Supported.Contains(audit.EventName))
            return VoidResult<string>.AsError("Unsupported plugin hook audit event.");
        var installation = await _installations.GetAsync(audit.InstallationId, cancellationToken).ConfigureAwait(false);
        if (installation.IsError) return VoidResult<string>.AsError(installation.Error);
        if (!HasHookComponent(installation.Value.ComponentInventoryJson, audit.HookComponentId))
            return VoidResult<string>.AsError("Plugin hook component is not declared by this installation.");
        var detailCode = string.IsNullOrWhiteSpace(audit.DetailCode) ? "none" : audit.DetailCode.Trim().ToLowerInvariant();
        if (!AuditDetailCodes.Contains(detailCode)) detailCode = "redacted";
        var outcome = string.IsNullOrWhiteSpace(audit.Outcome) ? "redacted" : audit.Outcome.Trim().ToLowerInvariant();
        if (!AuditOutcomes.Contains(outcome)) outcome = "redacted";
        return await _repository.AppendAuditAsync(new DysonPluginHookAuditEntity
        {
            InstallationId = audit.InstallationId, HookComponentId = audit.HookComponentId, EventName = audit.EventName,
            Outcome = outcome, DetailCode = detailCode,
            DurationMilliseconds = Math.Clamp(audit.DurationMilliseconds, 0, MaxTimeoutMilliseconds),
            InputBytes = Math.Clamp(audit.InputBytes, 0, int.MaxValue),
            OutputBytes = Math.Clamp(audit.OutputBytes, 0, MaxOutputBytes), OccurredUtc = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
    }


    private static bool HasHookComponent(string json, string hookComponentId)
    {
        try
        {
            var components = JsonSerializer.Deserialize<DysonResolvedPluginComponent[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            return components.Any(x => x.Kind == DysonPluginComponentKind.Hook && x.IsSupported && string.Equals(x.Id, hookComponentId, StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }
}

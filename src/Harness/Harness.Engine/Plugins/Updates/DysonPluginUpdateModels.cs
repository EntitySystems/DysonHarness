namespace DysonHarness;

public enum DysonPluginUpdateStatus
{
    Current = 0,
    UpdateAvailable = 1,
    Unsupported = 2,
}

/// <summary>
/// Checks an installed package without changing its scope or package files. GitHub sources are
/// reacquired from their stored provenance. Local sources require an explicit re-import request,
/// preventing background filesystem polling.
/// </summary>
public sealed record DysonPluginUpdateCheckRequest
{
    public required Guid InstallationId { get; init; }
    public DysonPluginPreviewRequest? LocalReimport { get; init; }

    public VoidResult<string> Validate()
    {
        if (InstallationId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin installation id is required.");

        if (LocalReimport is null)
            return VoidResult<string>.Success;

        var validation = DysonPluginRequestValidation.Validate(LocalReimport);
        if (validation.IsError)
            return validation;
        if (LocalReimport.SourceKind == DysonPluginSourceKind.GitHub)
        {
            return VoidResult<string>.AsError(
                "GitHub plugin update checks use the installed package provenance, not a supplied source.");
        }

        return VoidResult<string>.Success;
    }
}

/// <summary>
/// A check result includes the newly parsed package so callers can render identity, capabilities,
/// and diagnostics before they explicitly confirm an update. A retained preview id is supplied
/// only when package content has changed.
/// </summary>
public sealed record DysonPluginUpdateCheckResult
{
    public required DysonPluginUpdateStatus Status { get; init; }
    public required DysonPluginInstallationEntity Installation { get; init; }
    public DysonResolvedPlugin? Candidate { get; init; }
    public Guid? PreviewId { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Confirmation must use the retained preview returned by <see cref="DysonPluginUpdateService"/>
/// and the original installation's scope target. Updates never infer or move scope ownership.
/// </summary>
public sealed record DysonPluginUpdateRequest
{
    public required Guid InstallationId { get; init; }
    public required Guid PreviewId { get; init; }
    public required DysonPluginInstallTarget Target { get; init; }
    public required bool IsConfirmed { get; init; }

    public VoidResult<string> Validate()
    {
        if (InstallationId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin installation id is required.");
        if (PreviewId == Guid.Empty)
            return VoidResult<string>.AsError("A retained plugin update preview id is required.");
        if (Target is null)
            return VoidResult<string>.AsError("Plugin install target is required.");
        if (!IsConfirmed)
            return VoidResult<string>.AsError("Plugin update requires explicit user confirmation.");

        return Target.Validate();
    }
}

public sealed record DysonPluginUpdateResult
{
    public required DysonPluginInstallResult Installation { get; init; }
    public required bool LifecycleNotificationSucceeded { get; init; }
    public string? LifecycleNotificationError { get; init; }
}

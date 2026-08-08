using System.Collections.Concurrent;

namespace DysonHarness;

/// <summary>
/// Explicit, non-executing update workflow for subject-owned plugin installations. Candidate
/// package bytes are retained by <see cref="IDysonPluginPackageService"/> until confirmation.
/// </summary>
public sealed class DysonPluginUpdateService(
    IDysonPluginInstallationRepository installations,
    IDysonPluginPackageService packages,
    DysonPluginLifecycleService lifecycle)
{
    private readonly IDysonPluginInstallationRepository _installations =
        installations ?? throw new ArgumentNullException(nameof(installations));
    private readonly IDysonPluginPackageService _packages =
        packages ?? throw new ArgumentNullException(nameof(packages));
    private readonly DysonPluginLifecycleService _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    private readonly ConcurrentDictionary<Guid, Guid> _checkedPreviewOwners = new();

    public async Task<Result<DysonPluginUpdateCheckResult, string>> CheckAsync(
        DysonPluginUpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = request.Validate();
        if (validation.IsError)
            return Result<DysonPluginUpdateCheckResult, string>.AsError(validation.Error);

        var installed = await _installations.GetAsync(request.InstallationId, cancellationToken).ConfigureAwait(false);
        if (installed.IsError)
            return Result<DysonPluginUpdateCheckResult, string>.AsError(installed.Error);

        if (!Enum.TryParse<DysonPluginSourceKind>(installed.Value.SourceKind, out var sourceKind))
        {
            return Result<DysonPluginUpdateCheckResult, string>.AsValue(Unsupported(
                installed.Value, "The installed plugin has an unsupported source kind."));
        }

        var source = CreateCandidateSource(installed.Value, sourceKind, request.LocalReimport);
        if (source.IsError)
            return Result<DysonPluginUpdateCheckResult, string>.AsValue(Unsupported(installed.Value, source.Error));

        var preview = await _packages.PreviewAsync(source.Value, cancellationToken).ConfigureAwait(false);
        if (preview.IsError)
            return Result<DysonPluginUpdateCheckResult, string>.AsError(preview.Error);

        if (string.Equals(
                installed.Value.ContentChecksum,
                preview.Value.Plugin.Source.ContentChecksum,
                StringComparison.Ordinal))
        {
            await _packages.DiscardPreviewAsync(preview.Value.PreviewId, cancellationToken).ConfigureAwait(false);
            return Result<DysonPluginUpdateCheckResult, string>.AsValue(new DysonPluginUpdateCheckResult
            {
                Status = DysonPluginUpdateStatus.Current,
                Installation = installed.Value,
                Candidate = preview.Value.Plugin,
                Message = "The candidate package checksum matches the installed package.",
            });
        }

        _checkedPreviewOwners[preview.Value.PreviewId] = installed.Value.Id;
        return Result<DysonPluginUpdateCheckResult, string>.AsValue(new DysonPluginUpdateCheckResult
        {
            Status = DysonPluginUpdateStatus.UpdateAvailable,
            Installation = installed.Value,
            Candidate = preview.Value.Plugin,
            PreviewId = preview.Value.PreviewId,
            Message = CandidateSafetyMessage(preview.Value.Plugin),
        });
    }

    public async Task<Result<DysonPluginUpdateResult, string>> UpdateAsync(
        DysonPluginUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = request.Validate();
        if (validation.IsError)
            return Result<DysonPluginUpdateResult, string>.AsError(validation.Error);

        var installed = await _installations.GetAsync(request.InstallationId, cancellationToken).ConfigureAwait(false);
        if (installed.IsError)
            return Result<DysonPluginUpdateResult, string>.AsError(installed.Error);

        var ownership = ValidateTarget(installed.Value, request.Target);
        if (ownership.IsError)
            return Result<DysonPluginUpdateResult, string>.AsError(ownership.Error);
        if (!_checkedPreviewOwners.TryGetValue(request.PreviewId, out var previewOwner) ||
            previewOwner != request.InstallationId)
        {
            return Result<DysonPluginUpdateResult, string>.AsError(
                "Plugin update preview was not produced for this installation by this update service.");
        }

        var updated = await _packages.InstallAsync(new DysonPluginInstallRequest
        {
            PreviewId = request.PreviewId,
            Target = request.Target,
            ReplacesInstallationId = request.InstallationId,
        }, cancellationToken).ConfigureAwait(false);
        if (updated.IsError)
            return Result<DysonPluginUpdateResult, string>.AsError(updated.Error);
        _checkedPreviewOwners.TryRemove(request.PreviewId, out _);

        var notification = await _lifecycle.NotifyInstalledAsync(updated.Value.InstallationId, cancellationToken)
            .ConfigureAwait(false);
        return Result<DysonPluginUpdateResult, string>.AsValue(new DysonPluginUpdateResult
        {
            Installation = updated.Value,
            LifecycleNotificationSucceeded = notification.IsSuccess,
            LifecycleNotificationError = notification.IsError ? notification.Error : null,
        });
    }

    private static Result<DysonPluginPreviewRequest, string> CreateCandidateSource(
        DysonPluginInstallationEntity installed,
        DysonPluginSourceKind sourceKind,
        DysonPluginPreviewRequest? localReimport)
    {
        if (sourceKind == DysonPluginSourceKind.GitHub)
        {
            if (localReimport is not null)
                return Result<DysonPluginPreviewRequest, string>.AsError("GitHub updates cannot use a caller-supplied source.");
            return Result<DysonPluginPreviewRequest, string>.AsValue(new DysonPluginPreviewRequest
            {
                SourceKind = DysonPluginSourceKind.GitHub,
                SourceLocation = installed.SourceLocation,
                RequestedRef = installed.RequestedRef,
                PluginSubdirectory = installed.SourceSubdirectory,
            });
        }

        if (sourceKind is not (DysonPluginSourceKind.LocalZip or DysonPluginSourceKind.LocalFolder))
            return Result<DysonPluginPreviewRequest, string>.AsError("The installed plugin source does not support updates.");
        if (localReimport is null)
        {
            return Result<DysonPluginPreviewRequest, string>.AsError(
                "Local plugin updates require an explicit re-import; background filesystem polling is not supported.");
        }
        if (localReimport.SourceKind != sourceKind ||
            !string.Equals(localReimport.SourceLocation.Trim(), installed.SourceLocation, StringComparison.Ordinal))
        {
            return Result<DysonPluginPreviewRequest, string>.AsError(
                "Local re-import must retain the installed plugin source kind and location.");
        }

        return Result<DysonPluginPreviewRequest, string>.AsValue(localReimport);
    }

    private static VoidResult<string> ValidateTarget(
        DysonPluginInstallationEntity installation,
        DysonPluginInstallTarget target)
    {
        var expectedScope = target.Scope == DysonPluginInstallScope.Project
            ? DysonPluginStorageValues.ProjectScope
            : DysonPluginStorageValues.GlobalScope;
        if (!string.Equals(installation.InstallScope, expectedScope, StringComparison.Ordinal) ||
            installation.WorkDirectoryId != target.WorkDirectoryId)
        {
            return VoidResult<string>.AsError(
                "Plugin update target must match the installation scope and owning work directory.");
        }

        return DysonPluginPaths.ValidatePackageRootOwnership(target, installation.PackageRoot);
    }

    private static DysonPluginUpdateCheckResult Unsupported(
        DysonPluginInstallationEntity installation,
        string message) =>
        new()
        {
            Status = DysonPluginUpdateStatus.Unsupported,
            Installation = installation,
            Message = message,
        };

    private static string CandidateSafetyMessage(DysonResolvedPlugin candidate)
    {
        var executable = candidate.Capabilities.HasFlag(DysonPluginCapabilities.McpExecutable);
        var hooks = candidate.Capabilities.HasFlag(DysonPluginCapabilities.Hooks);
        return executable || hooks
            ? "Candidate contains executable MCP configuration or hooks and requires explicit user confirmation."
            : "Candidate is staged for explicit user confirmation.";
    }
}

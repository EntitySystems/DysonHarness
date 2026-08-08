namespace DysonHarness;

/// <summary>
/// Stable preview/install boundary. Preview is deliberately scope-independent; final paths are
/// selected only by <see cref="InstallAsync"/> after the caller supplies an explicit target.
/// </summary>
public interface IDysonPluginPackageService
{
    Task<Result<DysonPluginPreview, string>> PreviewAsync(
        DysonPluginPreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DysonPluginInstallResult, string>> InstallAsync(
        DysonPluginInstallRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a retained preview that will not be installed.</summary>
    Task<VoidResult<string>> DiscardPreviewAsync(
        Guid previewId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Scope-independent parser boundary for an already acquired/staged package. Implementations own
/// format detection, manifest validation, containment checks, and normalized component discovery.
/// </summary>
public interface IDysonPluginPackageParser
{
    Task<Result<DysonResolvedPlugin, string>> ParseAsync(
        DysonPluginParseRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DysonPluginParseRequest
{
    public required string StagedPackageRoot { get; init; }
    public required DysonPluginSource Source { get; init; }
    public DysonPluginPackageFormat? ExpectedFormat { get; init; }
}

public sealed record DysonPluginPreviewRequest
{
    public required DysonPluginSourceKind SourceKind { get; init; }
    public required string SourceLocation { get; init; }
    public ReadOnlyMemory<byte> ArchiveBytes { get; init; }
    public string? RequestedRef { get; init; }
    public string? PluginSubdirectory { get; init; }
}

public sealed record DysonPluginInstallRequest
{
    public required Guid PreviewId { get; init; }
    public required DysonPluginInstallTarget Target { get; init; }

    /// <summary>
    /// The installed record this confirmed operation replaces. When set, the package service
    /// preserves that record's scope ownership and only persists the replacement after its new
    /// immutable package directory has been promoted.
    /// </summary>
    public Guid? ReplacesInstallationId { get; init; }
}

public static class DysonPluginRequestValidation
{
    public static VoidResult<string> Validate(DysonPluginParseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.StagedPackageRoot) ||
            !Path.IsPathFullyQualified(request.StagedPackageRoot))
        {
            return VoidResult<string>.AsError("Staged plugin package root must be an absolute path.");
        }
        if (request.Source is null)
            return VoidResult<string>.AsError("Plugin source provenance is required.");
        if (request.ExpectedFormat is not null && !Enum.IsDefined(request.ExpectedFormat.Value))
        {
            return VoidResult<string>.AsError(
                $"Unsupported expected plugin package format: {request.ExpectedFormat.Value}.");
        }

        return VoidResult<string>.Success;
    }

    public static VoidResult<string> Validate(DysonPluginPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.SourceKind))
            return VoidResult<string>.AsError($"Unsupported plugin source kind: {request.SourceKind}.");
        if (string.IsNullOrWhiteSpace(request.SourceLocation))
            return VoidResult<string>.AsError("Plugin source location is required.");

        if (request.SourceKind == DysonPluginSourceKind.LocalZip && request.ArchiveBytes.IsEmpty)
            return VoidResult<string>.AsError("Plugin ZIP bytes are required.");
        if (request.SourceKind != DysonPluginSourceKind.LocalZip && !request.ArchiveBytes.IsEmpty)
            return VoidResult<string>.AsError("Plugin ZIP bytes are valid only for local ZIP sources.");

        if (request.SourceKind is not DysonPluginSourceKind.GitHub &&
            (!string.IsNullOrWhiteSpace(request.RequestedRef) ||
             !string.IsNullOrWhiteSpace(request.PluginSubdirectory)))
        {
            return VoidResult<string>.AsError(
                "Requested ref and plugin subdirectory are supported only for GitHub sources.");
        }

        return VoidResult<string>.Success;
    }

    public static VoidResult<string> Validate(DysonPluginInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PreviewId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin preview id is required.");

        if (request.Target is null)
            return VoidResult<string>.AsError("Plugin install target is required.");
        if (request.ReplacesInstallationId == Guid.Empty)
            return VoidResult<string>.AsError("Replacement plugin installation id must be non-empty when specified.");

        return request.Target.Validate();
    }
}

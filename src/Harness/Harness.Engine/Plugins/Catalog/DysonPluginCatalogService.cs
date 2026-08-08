using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Request for the plugins visible to a session. A null work-directory id intentionally exposes
/// only global installations; a non-null id is ownership-checked by the installation repository.
/// </summary>
public sealed record DysonPluginCatalogRequest
{
    public Guid? ActiveWorkDirectoryId { get; init; }

    public VoidResult<string> Validate() => ActiveWorkDirectoryId == Guid.Empty
        ? VoidResult<string>.AsError("Active work directory id must be non-empty when specified.")
        : VoidResult<string>.Success;
}

/// <summary>Persisted installation projected for catalog, inspection, and management consumers.</summary>
public sealed record DysonPluginCatalogInstallation
{
    public required DysonPluginInstallationEntity Installation { get; init; }
    public required DysonPluginStatus Status { get; init; }
    public required IReadOnlyList<DysonResolvedPluginComponent> Components { get; init; }
    public required IReadOnlyList<DysonPluginDiagnostic> Diagnostics { get; init; }

    /// <summary>
    /// Only enabled packages in a usable installed state may contribute session assets. An
    /// update-available package remains usable at its currently installed version.
    /// </summary>
    public bool IsReadyForCatalog => Installation.IsEnabled &&
        Status is DysonPluginStatus.Installed or DysonPluginStatus.UpdateAvailable;
}

/// <summary>
/// One normalized plugin identity in the effective session catalog. Project scope always wins over
/// global scope, including when the project record is disabled or otherwise not ready.
/// </summary>
public sealed record DysonEffectivePluginCatalogEntry
{
    public required string NormalizedPluginId { get; init; }
    public required DysonPluginCatalogInstallation EffectiveInstallation { get; init; }
    public IReadOnlyList<DysonPluginCatalogInstallation> ShadowedGlobalInstallations { get; init; } = [];
    public DysonPluginCatalogInstallation? ShadowedGlobalInstallation =>
        ShadowedGlobalInstallations.FirstOrDefault();
}

/// <summary>Supported components of a ready effective package for session catalog builders.</summary>
public sealed record DysonPluginActiveContribution
{
    public required DysonPluginCatalogInstallation Installation { get; init; }
    public required IReadOnlyList<DysonResolvedPluginComponent> Components { get; init; }
}

public sealed record DysonEffectivePluginCatalog
{
    public Guid? ActiveWorkDirectoryId { get; init; }
    public IReadOnlyList<DysonEffectivePluginCatalogEntry> Entries { get; init; } = [];
    public IReadOnlyList<DysonPluginActiveContribution> ActiveContributions { get; init; } = [];
}

public sealed record DysonPluginInspection
{
    public required DysonPluginCatalogInstallation Installation { get; init; }
}

/// <summary>
/// Builds the session-effective plugin view from global packages and, when supplied, only the
/// active subject-owned work directory. It is deliberately read-only: acquisition owns creation
/// and promotion of installation records.
/// </summary>
public sealed class DysonPluginCatalogService(IDysonPluginInstallationRepository installations)
{
    private readonly IDysonPluginInstallationRepository _installations =
        installations ?? throw new ArgumentNullException(nameof(installations));

    public async Task<Result<DysonEffectivePluginCatalog, string>> GetEffectiveCatalogAsync(
        DysonPluginCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = request.Validate();
        if (validation.IsError)
            return Result<DysonEffectivePluginCatalog, string>.AsError(validation.Error);

        var listed = await _installations.ListAsync(request.ActiveWorkDirectoryId, cancellationToken)
            .ConfigureAwait(false);
        if (listed.IsError)
            return Result<DysonEffectivePluginCatalog, string>.AsError(listed.Error);

        var projected = listed.Value.Select(Project).ToList();
        var entries = BuildEntries(projected);
        var active = entries
            .Where(entry => entry.EffectiveInstallation.IsReadyForCatalog)
            .Select(entry => new DysonPluginActiveContribution
            {
                Installation = entry.EffectiveInstallation,
                // Retain the raw persisted inventory for runtime revalidation. Individual consumers
                // must decide which supported component kinds they can expose.
                Components = entry.EffectiveInstallation.Components,
            })
            .ToArray();

        return Result<DysonEffectivePluginCatalog, string>.AsValue(new DysonEffectivePluginCatalog
        {
            ActiveWorkDirectoryId = request.ActiveWorkDirectoryId,
            Entries = entries,
            ActiveContributions = active,
        });
    }

    public async Task<Result<DysonPluginInspection, string>> InspectAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty)
            return Result<DysonPluginInspection, string>.AsError("Plugin installation id is required.");

        var installation = await _installations.GetAsync(installationId, cancellationToken)
            .ConfigureAwait(false);
        return installation.IsError
            ? Result<DysonPluginInspection, string>.AsError(installation.Error)
            : Result<DysonPluginInspection, string>.AsValue(new DysonPluginInspection
            {
                Installation = Project(installation.Value),
            });
    }

    public async Task<Result<IReadOnlyList<DysonPluginDiagnostic>, string>> GetDiagnosticsAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(installationId, cancellationToken).ConfigureAwait(false);
        return inspection.IsError
            ? Result<IReadOnlyList<DysonPluginDiagnostic>, string>.AsError(inspection.Error)
            : Result<IReadOnlyList<DysonPluginDiagnostic>, string>.AsValue(
                inspection.Value.Installation.Diagnostics);
    }

    public async Task<Result<DysonPluginStatus, string>> GetStatusAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(installationId, cancellationToken).ConfigureAwait(false);
        return inspection.IsError
            ? Result<DysonPluginStatus, string>.AsError(inspection.Error)
            : Result<DysonPluginStatus, string>.AsValue(inspection.Value.Installation.Status);
    }

    private static IReadOnlyList<DysonEffectivePluginCatalogEntry> BuildEntries(
        IReadOnlyList<DysonPluginCatalogInstallation> installations)
    {
        var entries = new List<DysonEffectivePluginCatalogEntry>();
        foreach (var group in installations.GroupBy(
                     item => item.Installation.NormalizedPluginId,
                     StringComparer.Ordinal))
        {
            var globals = group
                .Where(item => item.Installation.InstallScope == DysonPluginStorageValues.GlobalScope)
                .OrderByDescending(item => item.Installation.UpdatedUtc)
                .ThenByDescending(item => item.Installation.Id)
                .ToArray();
            var projects = group
                .Where(item => item.Installation.InstallScope == DysonPluginStorageValues.ProjectScope)
                .OrderByDescending(item => item.Installation.UpdatedUtc)
                .ThenByDescending(item => item.Installation.Id)
                .ToArray();

            // The repository returns project rows only for the requested active work directory.
            // Thus a project row is the effective package without leaking other projects.
            var effective = projects.FirstOrDefault() ?? globals.FirstOrDefault();
            if (effective is null)
                continue;

            entries.Add(new DysonEffectivePluginCatalogEntry
            {
                NormalizedPluginId = group.Key,
                EffectiveInstallation = effective,
                ShadowedGlobalInstallations = projects.Length == 0 ? [] : globals,
            });
        }

        return entries.OrderBy(entry => entry.NormalizedPluginId, StringComparer.Ordinal).ToArray();
    }

    private static DysonPluginCatalogInstallation Project(DysonPluginInstallationEntity installation)
    {
        var diagnostics = DeserializeList<DysonPluginDiagnostic>(
            installation.DiagnosticsJson,
            "diagnostics",
            installation.Id);
        var components = DeserializeList<DysonResolvedPluginComponent>(
            installation.ComponentInventoryJson,
            "component inventory",
            installation.Id);

        var allDiagnostics = diagnostics.ParseDiagnostic is null && components.ParseDiagnostic is null
            ? diagnostics.Items
            : diagnostics.Items
                .Concat(new[] { diagnostics.ParseDiagnostic, components.ParseDiagnostic }
                    .OfType<DysonPluginDiagnostic>())
                .ToArray();

        return new DysonPluginCatalogInstallation
        {
            Installation = installation,
            Status = ParseStatus(installation.Status),
            Components = components.Items,
            Diagnostics = allDiagnostics,
        };
    }

    private static DysonPluginStatus ParseStatus(string value) =>
        Enum.TryParse<DysonPluginStatus>(value, ignoreCase: false, out var status)
            ? status
            : DysonPluginStatus.Invalid;

    private static DeserializedList<T> DeserializeList<T>(string json, string label, Guid installationId)
    {
        try
        {
            return new DeserializedList<T>(JsonSerializer.Deserialize<List<T>>(json) ?? []);
        }
        catch (JsonException ex)
        {
            return new DeserializedList<T>([], new DysonPluginDiagnostic
            {
                Severity = DysonPluginDiagnosticSeverity.Error,
                Code = "stored-plugin-json-invalid",
                Message = $"Stored plugin {label} could not be read for installation '{installationId}': {ex.Message}",
            });
        }
    }

    private sealed record DeserializedList<T>(
        IReadOnlyList<T> Items,
        DysonPluginDiagnostic? ParseDiagnostic = null);
}

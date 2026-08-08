namespace DysonHarness;

public sealed class DysonPluginMcpGrantChangedEventArgs : EventArgs
{
    public required Guid InstallationId { get; init; }
    public required DysonPluginInstallScope Scope { get; init; }
    public Guid? WorkDirectoryId { get; init; }
}

/// <summary>
/// Validates, persists, and projects explicit managed plugin MCP runtime grants. Grants are bound
/// to the current installation checksum so package updates automatically return to deny-all.
/// </summary>
public sealed class DysonPluginMcpGrantService(
    IDysonPluginInstallationRepository installations,
    IDysonPluginMcpGrantRepository grants,
    DysonPluginCatalogService catalogService,
    DysonPluginMcpResolver resolver)
{
    private readonly IDysonPluginInstallationRepository _installations =
        installations ?? throw new ArgumentNullException(nameof(installations));
    private readonly IDysonPluginMcpGrantRepository _grants =
        grants ?? throw new ArgumentNullException(nameof(grants));
    private readonly DysonPluginCatalogService _catalogService =
        catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly DysonPluginMcpResolver _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));

    public event EventHandler<DysonPluginMcpGrantChangedEventArgs>? Changed;

    public async Task<VoidResult<string>> GrantAsync(
        Guid installationId,
        string serverId,
        DysonPluginMcpRuntimeCapability capabilities,
        CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin installation id is required.");
        if (string.IsNullOrWhiteSpace(serverId))
            return VoidResult<string>.AsError("Plugin MCP server id is required.");
        if (capabilities == DysonPluginMcpRuntimeCapability.None ||
            (capabilities & ~(DysonPluginMcpRuntimeCapability.Executable |
                              DysonPluginMcpRuntimeCapability.Network)) != 0)
        {
            return VoidResult<string>.AsError("Plugin MCP grant capabilities are invalid.");
        }

        var installation = await _installations.GetAsync(installationId, cancellationToken)
            .ConfigureAwait(false);
        if (installation.IsError)
            return VoidResult<string>.AsError(installation.Error);
        if (!installation.Value.IsEnabled ||
            (!string.Equals(installation.Value.Status, nameof(DysonPluginStatus.Installed), StringComparison.Ordinal) &&
             !string.Equals(installation.Value.Status, nameof(DysonPluginStatus.UpdateAvailable), StringComparison.Ordinal)))
        {
            return VoidResult<string>.AsError("Plugin installation is not enabled and ready for runtime activation.");
        }
        if (string.IsNullOrWhiteSpace(installation.Value.ContentChecksum))
            return VoidResult<string>.AsError("Plugin installation has no content checksum to bind the grant to.");

        var declared = await ResolveServerAsync(installation.Value, serverId.Trim(), cancellationToken)
            .ConfigureAwait(false);
        if (declared.IsError)
            return VoidResult<string>.AsError(declared.Error);
        var requiredCapability = declared.Value.Transport switch
        {
            DysonPluginMcpTransportKind.Stdio => DysonPluginMcpRuntimeCapability.Executable,
            DysonPluginMcpTransportKind.StreamableHttp or DysonPluginMcpTransportKind.Sse =>
                DysonPluginMcpRuntimeCapability.Network,
            _ => DysonPluginMcpRuntimeCapability.None,
        };
        if (requiredCapability == DysonPluginMcpRuntimeCapability.None)
            return VoidResult<string>.AsError("Plugin MCP server transport is unavailable for approval.");
        if (capabilities != requiredCapability)
        {
            return VoidResult<string>.AsError(
                $"Plugin MCP server requires the '{requiredCapability}' capability grant.");
        }

        var persisted = await _grants.UpsertAsync(new DysonPluginMcpGrantEntity
        {
            InstallationId = installationId,
            ServerId = serverId.Trim(),
            Capabilities = (int)capabilities,
            PackageChecksum = installation.Value.ContentChecksum,
            GrantedUtc = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        if (persisted.IsError)
            return persisted;

        RaiseChanged(installation.Value);
        return VoidResult<string>.Success;
    }

    public async Task<VoidResult<string>> RevokeAsync(
        Guid installationId,
        string serverId,
        CancellationToken cancellationToken = default)
    {
        var installation = await _installations.GetAsync(installationId, cancellationToken)
            .ConfigureAwait(false);
        if (installation.IsError)
            return VoidResult<string>.AsError(installation.Error);

        var revoked = await _grants.RevokeAsync(
            installationId, serverId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (revoked.IsError)
            return revoked;

        RaiseChanged(installation.Value);
        return VoidResult<string>.Success;
    }

    public async Task<Result<DysonPluginMcpRuntimeActivation, string>> BuildActivationAsync(
        DysonEffectivePluginCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var listed = await _grants.ListAsync(catalog.ActiveWorkDirectoryId, cancellationToken)
            .ConfigureAwait(false);
        if (listed.IsError)
            return Result<DysonPluginMcpRuntimeActivation, string>.AsError(listed.Error);

        var installationsById = catalog.ActiveContributions.ToDictionary(
            contribution => contribution.Installation.Installation.Id,
            contribution => contribution.Installation.Installation);
        var runtimeGrants = new List<DysonPluginMcpRuntimeGrant>();
        foreach (var row in listed.Value)
        {
            if (row.RevokedUtc is not null ||
                !installationsById.TryGetValue(row.InstallationId, out var installation) ||
                string.IsNullOrWhiteSpace(installation.ContentChecksum) ||
                !string.Equals(row.PackageChecksum, installation.ContentChecksum, StringComparison.Ordinal))
            {
                continue;
            }

            var capabilities = (DysonPluginMcpRuntimeCapability)row.Capabilities;
            if (capabilities == DysonPluginMcpRuntimeCapability.None ||
                (capabilities & ~(DysonPluginMcpRuntimeCapability.Executable |
                                  DysonPluginMcpRuntimeCapability.Network)) != 0)
            {
                continue;
            }

            runtimeGrants.Add(new DysonPluginMcpRuntimeGrant
            {
                InstallationId = row.InstallationId,
                ServerId = row.ServerId,
                Capabilities = capabilities,
            });
        }

        return Result<DysonPluginMcpRuntimeActivation, string>.AsValue(new DysonPluginMcpRuntimeActivation
        {
            Grants = runtimeGrants,
        });
    }

    private async Task<Result<DysonPluginMcpServerDeclaration, string>> ResolveServerAsync(
        DysonPluginInstallationEntity installation,
        string serverId,
        CancellationToken cancellationToken)
    {
        var scope = string.Equals(
            installation.InstallScope, DysonPluginStorageValues.ProjectScope, StringComparison.Ordinal)
            ? installation.WorkDirectoryId
            : null;
        var catalog = await _catalogService.GetEffectiveCatalogAsync(new DysonPluginCatalogRequest
        {
            ActiveWorkDirectoryId = scope,
        }, cancellationToken).ConfigureAwait(false);
        if (catalog.IsError)
            return Result<DysonPluginMcpServerDeclaration, string>.AsError(catalog.Error);
        var resolved = _resolver.Resolve(catalog.Value);
        if (resolved.IsError)
            return Result<DysonPluginMcpServerDeclaration, string>.AsError(resolved.Error);
        var declaration = resolved.Value.Servers.FirstOrDefault(server =>
            server.InstallationId == installation.Id &&
            string.Equals(server.ServerId, serverId, StringComparison.Ordinal));
        return declaration is null
            ? Result<DysonPluginMcpServerDeclaration, string>.AsError(
                $"Plugin MCP server '{serverId}' is not declared by this installation.")
            : Result<DysonPluginMcpServerDeclaration, string>.AsValue(declaration);
    }

    private void RaiseChanged(DysonPluginInstallationEntity installation) => Changed?.Invoke(this,
        new DysonPluginMcpGrantChangedEventArgs
        {
            InstallationId = installation.Id,
            Scope = string.Equals(installation.InstallScope, DysonPluginStorageValues.ProjectScope,
                StringComparison.Ordinal)
                ? DysonPluginInstallScope.Project
                : DysonPluginInstallScope.Global,
            WorkDirectoryId = installation.WorkDirectoryId,
        });
}

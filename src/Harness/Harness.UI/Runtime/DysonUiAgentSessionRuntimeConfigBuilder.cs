namespace DysonHarness;

/// <summary>
/// Inputs for retained-scope non-UI session config. Theme is an immutable snapshot — this
/// builder never captures <c>ThemeService</c>, browser control, or circuit state.
/// </summary>
internal sealed class DysonUiAgentSessionRuntimeConfigRequest
{
    public DysonUiThemeSnapshot Theme { get; init; } = DysonUiThemeSnapshot.Default;

    public string? AgentMode { get; init; }

    public DysonMcpAccessMode? McpAccessMode { get; init; }

    public Guid? WorkDirectoryId { get; init; }

    /// <summary>
    /// Optional absolute work-directory path. When omitted and
    /// <see cref="WorkDirectoryId"/> is set, the builder looks it up.
    /// </summary>
    public string? WorkRoot { get; init; }
}

/// <summary>
/// Built non-UI session config plus disposal of factory-created MCP resource leases.
/// </summary>
internal sealed class DysonUiAgentSessionRuntimeConfigLease : IAsyncDisposable
{
    private int _disposed;

    public DysonUiAgentSessionRuntimeConfigLease(
        DysonAgentSessionConfig config,
        IReadOnlyList<string>? diagnostics = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Diagnostics = diagnostics ?? [];
    }

    public DysonAgentSessionConfig Config { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await DysonUiAgentSessionRuntimeConfigBuilder
            .ReleaseMcpForConfigAsync(Config)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Retained-scope composition of the same non-UI <see cref="DysonAgentSessionConfig"/> that
/// <c>DysonUiHost.BuildSessionConfigAsync</c> builds for a root demo session. Does not resolve
/// theme, browser, Razor, JS, or host/circuit services.
/// </summary>
internal sealed class DysonUiAgentSessionRuntimeConfigBuilder(
    IDysonWorkDirectoryRepository workDirectories,
    IDysonWorkDirectoryConfigurationRepository workDirectoryConfigurations,
    IDysonSubjectSettingsRepository appSettings,
    IDysonConfiguredShellRepository configuredShells,
    IDysonModelRepository models,
    DysonPluginCatalogService pluginCatalog,
    DysonPluginContributionResolver pluginContributions,
    DysonPluginMcpGrantService pluginMcpGrants,
    DysonPluginMcpResolver pluginMcpResolver)
{
    private readonly IDysonWorkDirectoryRepository _workDirectories =
        workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));
    private readonly IDysonWorkDirectoryConfigurationRepository _workDirectoryConfigurations =
        workDirectoryConfigurations ?? throw new ArgumentNullException(nameof(workDirectoryConfigurations));
    private readonly IDysonSubjectSettingsRepository _appSettings =
        appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    private readonly IDysonConfiguredShellRepository _configuredShells =
        configuredShells ?? throw new ArgumentNullException(nameof(configuredShells));
    private readonly IDysonModelRepository _models =
        models ?? throw new ArgumentNullException(nameof(models));
    private readonly DysonPluginCatalogService _pluginCatalog =
        pluginCatalog ?? throw new ArgumentNullException(nameof(pluginCatalog));
    private readonly DysonPluginContributionResolver _pluginContributions =
        pluginContributions ?? throw new ArgumentNullException(nameof(pluginContributions));
    private readonly DysonPluginMcpGrantService _pluginMcpGrants =
        pluginMcpGrants ?? throw new ArgumentNullException(nameof(pluginMcpGrants));
    private readonly DysonPluginMcpResolver _pluginMcpResolver =
        pluginMcpResolver ?? throw new ArgumentNullException(nameof(pluginMcpResolver));

    public async Task<Result<DysonUiAgentSessionRuntimeConfigLease, string>> BuildAsync(
        DysonUiAgentSessionRuntimeConfigRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var workDirectoryId = NormalizeWorkDirectoryId(request.WorkDirectoryId);
        var workRoot = request.WorkRoot;
        if (workDirectoryId is Guid wd && string.IsNullOrWhiteSpace(workRoot))
        {
            var workDirectory = await _workDirectories
                .GetAsync(wd, cancellationToken)
                .ConfigureAwait(false);
            if (workDirectory.IsError)
                return Result<DysonUiAgentSessionRuntimeConfigLease, string>.AsError(workDirectory.Error);

            workRoot = workDirectory.Value.AbsolutePath;
        }

        var diagnostics = new List<string>();
        var contributions = await ResolvePluginContributionsAsync(
                workDirectoryId, diagnostics, cancellationToken)
            .ConfigureAwait(false);

        var config = new DysonAgentSessionConfig
        {
            PluginContributions = contributions,
            UiTheme = request.Theme,
        };
        MergePluginCustomAgents(config, contributions);
        if (request.McpAccessMode is { } mode)
            config.McpAccessMode = mode;

        var ensureShells = await _configuredShells.EnsureDefaultsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!ensureShells.IsError)
        {
            var shells = await _configuredShells.ListEnabledSpecsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!shells.IsError)
                config.AvailableShells = shells.Value;
        }

        var policyStore = new DysonToolPolicyStore(_appSettings);
        var policy = await policyStore.GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (!policy.IsError)
        {
            config.ToolPolicy = policy.Value;
            if (!string.IsNullOrWhiteSpace(request.AgentMode))
            {
                config.DisabledTools = DysonToolPolicyResolver.Resolve(
                    policy.Value, request.AgentMode.Trim());
            }
        }

        var transferred = false;
        try
        {
            if (workDirectoryId is Guid customWd && !string.IsNullOrWhiteSpace(workRoot))
            {
                var mcpActive = true;
                var cfg = await _workDirectoryConfigurations.GetAsync(customWd, cancellationToken)
                    .ConfigureAwait(false);
                if (!cfg.IsError)
                    mcpActive = DysonWorkDirectoryConfig.TryGetMcpActive(cfg.Value);

                var host = DysonCustomMcpHostRegistry.Retain(customWd, workRoot, mcpActive);
                if (host.McpActive != mcpActive)
                    host.SetMcpActive(mcpActive);
                host.PromptUpdater.StartWatcher();
                host.PromptUpdater.EnqueueRefresh();
                config.CustomMcpHost = host;
            }

            config.PluginMcpWorkDirectoryId = workDirectoryId;
            var pluginCatalog = await _pluginCatalog.GetEffectiveCatalogAsync(new DysonPluginCatalogRequest
            {
                ActiveWorkDirectoryId = workDirectoryId,
            }, cancellationToken).ConfigureAwait(false);
            if (pluginCatalog.IsError)
            {
                diagnostics.Add($"Plugin MCP catalog was unavailable: {pluginCatalog.Error}");
            }
            else
            {
                var activation = await _pluginMcpGrants
                    .BuildActivationAsync(pluginCatalog.Value, cancellationToken)
                    .ConfigureAwait(false);
                var effectiveActivation = activation.IsError
                    ? DysonPluginMcpRuntimeActivation.DenyAll
                    : activation.Value;
                if (activation.IsError)
                    diagnostics.Add($"Plugin MCP grants were unavailable: {activation.Error}");

                var pluginHost = new DysonPluginMcpHost(_pluginMcpResolver);
                var refreshed = await pluginHost.RefreshAsync(
                    pluginCatalog.Value,
                    effectiveActivation,
                    BuildPluginMcpReservedNames(config),
                    cancellationToken).ConfigureAwait(false);
                if (refreshed.IsError)
                {
                    diagnostics.Add($"Plugin MCP runtime was unavailable: {refreshed.Error}");
                    await pluginHost.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    config.PluginMcpHost = pluginHost;
                }
            }

            await TryHydrateOpenAiProviderSettingAsync(
                    DysonAppSettingKeys.WebSearchSummarizerModelSlugId,
                    p => config.SummarizerProvider = p,
                    cancellationToken)
                .ConfigureAwait(false);
            await TryHydrateOpenAiProviderSettingAsync(
                    DysonAppSettingKeys.TurnSummarizerModelSlugId,
                    p => config.TurnSummarizerProvider = p,
                    cancellationToken)
                .ConfigureAwait(false);
            await TryHydrateOpenAiProviderSettingAsync(
                    DysonAppSettingKeys.ExploreModelSlugId,
                    p => config.ExploreDefaultProvider = p,
                    cancellationToken)
                .ConfigureAwait(false);
            await TryHydrateOpenAiProviderSettingAsync(
                    DysonAppSettingKeys.DroneModelSlugId,
                    p => config.DroneDefaultProvider = p,
                    cancellationToken)
                .ConfigureAwait(false);
            await TryHydrateOpenAiProviderSettingAsync(
                    DysonAppSettingKeys.SecurityReviewModelSlugId,
                    p => config.SecurityReviewDefaultProvider = p,
                    cancellationToken)
                .ConfigureAwait(false);
            await TryHydrateOpenAiProviderSettingAsync(
                    DysonAppSettingKeys.BugReviewModelSlugId,
                    p => config.BugReviewDefaultProvider = p,
                    cancellationToken)
                .ConfigureAwait(false);

            var lease = new DysonUiAgentSessionRuntimeConfigLease(config, diagnostics);
            transferred = true;
            return Result<DysonUiAgentSessionRuntimeConfigLease, string>.AsValue(lease);
        }
        finally
        {
            if (!transferred)
                await ReleaseMcpForConfigAsync(config).ConfigureAwait(false);
        }
    }

    internal static async Task ReleaseMcpForConfigAsync(DysonAgentSessionConfig config)
    {
        if (config.CustomMcpHost is { } customHost)
        {
            await DysonCustomMcpHostRegistry.ReleaseAsync(customHost.WorkDirectoryId).ConfigureAwait(false);
            config.CustomMcpHost = null;
        }

        if (config.PluginMcpHost is { } pluginHost)
        {
            await pluginHost.DisposeAsync().ConfigureAwait(false);
            config.PluginMcpHost = null;
        }
    }

    private async Task<DysonPluginContributionSet> ResolvePluginContributionsAsync(
        Guid? workDirectoryId,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var catalog = await _pluginCatalog.GetEffectiveCatalogAsync(new DysonPluginCatalogRequest
        {
            ActiveWorkDirectoryId = workDirectoryId,
        }, cancellationToken).ConfigureAwait(false);
        if (catalog.IsError)
        {
            diagnostics.Add($"Plugin contributions were unavailable: {catalog.Error}");
            return new DysonPluginContributionSet
            {
                Diagnostics =
                [
                    new DysonPluginDiagnostic
                    {
                        Severity = DysonPluginDiagnosticSeverity.Error,
                        Code = "plugin-catalog-unavailable",
                        Message = catalog.Error,
                    },
                ],
            };
        }

        var resolved = _pluginContributions.Resolve(catalog.Value);
        if (resolved.IsError)
        {
            diagnostics.Add($"Plugin contributions were unavailable: {resolved.Error}");
            return new DysonPluginContributionSet
            {
                Diagnostics =
                [
                    new DysonPluginDiagnostic
                    {
                        Severity = DysonPluginDiagnosticSeverity.Error,
                        Code = "plugin-contribution-resolution-failed",
                        Message = resolved.Error,
                    },
                ],
            };
        }

        var contributionError = resolved.Value.Diagnostics.FirstOrDefault(diagnostic =>
            diagnostic.Severity == DysonPluginDiagnosticSeverity.Error);
        if (contributionError is not null)
            diagnostics.Add($"Plugin contribution diagnostic: {contributionError.Message}");

        return resolved.Value;
    }

    private static void MergePluginCustomAgents(
        DysonAgentSessionConfig config,
        DysonPluginContributionSet contributions)
    {
        foreach (var agent in contributions.ToCustomAgentPrompts()
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (DysonAgentModes.BuiltIns.Contains(agent.Key, StringComparer.OrdinalIgnoreCase)
                || config.CustomAgents.ContainsKey(agent.Key)
                || string.IsNullOrWhiteSpace(agent.Value))
            {
                continue;
            }

            config.CustomAgents.Add(agent.Key, agent.Value);
        }
    }

    private static IReadOnlySet<string> BuildPluginMcpReservedNames(DysonAgentSessionConfig config)
    {
        var names = new HashSet<string>(
            DysonSessionToolsetBuilder.AllCatalogToolNames(),
            StringComparer.Ordinal);
        if (config.CustomMcpHost is { } custom)
        {
            foreach (var name in custom.ToolMap.ByCatalog.Keys)
                names.Add(name);
        }

        return names;
    }

    private async Task TryHydrateOpenAiProviderSettingAsync(
        string settingKey,
        Action<OpenAiCompatibleAgentProvider> assign,
        CancellationToken cancellationToken)
    {
        var setting = await _appSettings
            .GetSettingAsync(settingKey, cancellationToken)
            .ConfigureAwait(false);

        if (setting.IsError
            || string.IsNullOrWhiteSpace(setting.Value)
            || !Guid.TryParse(setting.Value, out var slugId)
            || slugId == Guid.Empty)
        {
            return;
        }

        var slugResult = await _models.GetSlugAsync(slugId, cancellationToken).ConfigureAwait(false);
        if (slugResult.IsError
            || slugResult.Value is null
            || !IsOpenAiCompatibleSlug(slugResult.Value))
        {
            return;
        }

        assign(new OpenAiCompatibleAgentProvider(slugResult.Value));
    }

    private static bool IsOpenAiCompatibleSlug(DysonModelSlugEntity slug)
    {
        var provider = slug.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);
        return string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal);
    }

    private static Guid? NormalizeWorkDirectoryId(Guid? workDirectoryId) =>
        workDirectoryId is Guid id && id != Guid.Empty ? id : null;
}

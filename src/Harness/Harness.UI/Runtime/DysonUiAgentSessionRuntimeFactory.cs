using Harness.UI.Demo;

namespace DysonHarness;

/// <summary>
/// Retained-scope composition factory. <see cref="CreateRootAsync"/> and <see cref="LoadAsync"/>
/// create or resume demo and OpenAI-compatible sessions from subject-scoped repositories.
/// OpenAI-compatible covers every built-in agent mode; Completions vs Responses follows the
/// provider row. Does not resolve circuit/UI services.
/// </summary>
internal sealed class DysonUiAgentSessionRuntimeFactory(
    IDysonSessionRepository sessions,
    IDysonModelRepository models,
    IDysonWorkDirectoryRepository workDirectories,
    IDysonWorkDirectoryConfigurationRepository workDirectoryConfigurations,
    DysonWorkDirectoryService workDirectoryService,
    DysonUiAgentSessionRuntimeConfigBuilder configBuilder,
    HttpClient? http = null,
    IDysonPlanRepository? plans = null,
    IDysonUsageAnalyticsRepository? usageAnalytics = null,
    DysonCliProxyHost? cliProxy = null)
    : IDysonAgentSessionRuntimeFactory
{
    private readonly IDysonSessionRepository _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IDysonModelRepository _models =
        models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonWorkDirectoryRepository _workDirectories =
        workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));
    private readonly IDysonWorkDirectoryConfigurationRepository _workDirectoryConfigurations =
        workDirectoryConfigurations ?? throw new ArgumentNullException(nameof(workDirectoryConfigurations));
    private readonly DysonWorkDirectoryService _workDirectoryService =
        workDirectoryService ?? throw new ArgumentNullException(nameof(workDirectoryService));
    private readonly DysonUiAgentSessionRuntimeConfigBuilder _configBuilder =
        configBuilder ?? throw new ArgumentNullException(nameof(configBuilder));
    private readonly HttpClient? _http = http;
    private readonly IDysonPlanRepository? _plans = plans;
    private readonly IDysonUsageAnalyticsRepository? _usageAnalytics = usageAnalytics;
    private readonly DysonCliProxyHost? _cliProxy = cliProxy;

    public async Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
        DysonAgentSessionRuntimeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.AgentMode))
            return Result<DysonAgentSessionRuntimeLease, string>.AsError("Agent mode is required.");

        if (request.WorkDirectoryId == Guid.Empty)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError("Work directory is required.");

        var workDirectory = await _workDirectories
            .GetAsync(request.WorkDirectoryId, cancellationToken)
            .ConfigureAwait(false);
        if (workDirectory.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(workDirectory.Error);

        await _workDirectoryService
            .RefreshGitOriginAsync(request.WorkDirectoryId, cancellationToken)
            .ConfigureAwait(false);

        var provider = await ResolveProviderAsync(
                request.ModelSlugId,
                request.ReasoningEffort,
                cancellationToken)
            .ConfigureAwait(false);
        if (provider.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(provider.Error);

        if (provider.Value.OpenAi is not null
            && string.IsNullOrWhiteSpace(workDirectory.Value.AbsolutePath))
        {
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(
                "Work directory path is required.");
        }

        var builtConfig = await _configBuilder.BuildAsync(
                new DysonUiAgentSessionRuntimeConfigRequest
                {
                    Theme = request.Theme,
                    AgentMode = request.AgentMode,
                    WorkDirectoryId = request.WorkDirectoryId,
                    WorkRoot = workDirectory.Value.AbsolutePath,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (builtConfig.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(builtConfig.Error);

        var forkWorktree = false;
        var cfg = await _workDirectoryConfigurations
            .GetAsync(request.WorkDirectoryId, cancellationToken)
            .ConfigureAwait(false);
        if (cfg.IsSuccess)
            forkWorktree = DysonWorkDirectoryConfig.TryGetForkWorktree(cfg.Value);

        var configLease = builtConfig.Value;
        DysonAgentSession session;
        if (provider.Value.OpenAi is { } openAi)
        {
            var created = await OpenAiCompatibleAgentSession.CreateAsync(
                    _sessions,
                    openAi,
                    _http!,
                    request.WorkDirectoryId,
                    workDirectory.Value.AbsolutePath,
                    request.AgentMode,
                    config: configLease.Config,
                    models: _models,
                    usageAnalytics: _usageAnalytics,
                    plans: _plans,
                    workDirectoryName: workDirectory.Value.Name,
                    worktreeEnabled: forkWorktree,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
            {
                await configLease.DisposeAsync().ConfigureAwait(false);
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(created.Error);
            }

            session = created.Value;
        }
        else
        {
            var created = await DemoDysonAgentSession.CreateAsync(
                    _sessions,
                    provider.Value.Demo!,
                    request.WorkDirectoryId,
                    request.AgentMode,
                    config: configLease.Config,
                    models: _models,
                    workDirectoryAbsolutePath: workDirectory.Value.AbsolutePath,
                    worktreeEnabled: forkWorktree,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
            {
                await configLease.DisposeAsync().ConfigureAwait(false);
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(created.Error);
            }

            session = created.Value;
        }

        if (request.MaxTargetContextTokens is int requestedMax)
        {
            var normalized = DysonMaxTargetContextTokens.Normalize(requestedMax);
            session.MaxTargetContextTokens = normalized;
            if (session.PersistenceId != Guid.Empty)
            {
                var persist = await _sessions.UpdateSessionMetaAsync(
                        new DysonSessionMetaUpdate
                        {
                            SessionId = session.PersistenceId,
                            UpdateMaxTargetContextTokens = true,
                            MaxTargetContextTokens = normalized,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (persist.IsError)
                {
                    await configLease.DisposeAsync().ConfigureAwait(false);
                    return Result<DysonAgentSessionRuntimeLease, string>.AsError(persist.Error);
                }
            }
        }

        return Result<DysonAgentSessionRuntimeLease, string>.AsValue(
            new DysonAgentSessionRuntimeLease(session, configLease.DisposeAsync));
    }

    public async Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError("Session id is required.");

        var full = await _sessions.GetFullSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (full.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(full.Error);

        var persisted = full.Value.Session;
        var provider = await ResolveProviderAsync(
                persisted.ModelSlugId,
                persisted.ReasoningEffort,
                cancellationToken)
            .ConfigureAwait(false);
        if (provider.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(provider.Error);

        string? registeredPath = null;
        string? workPath = null;
        var workDirectoryName = "";
        if (persisted.WorkDirectoryId is Guid workDirectoryId && workDirectoryId != Guid.Empty)
        {
            var workDirectory = await _workDirectories
                .GetAsync(workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (workDirectory.IsError)
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(workDirectory.Error);

            await _workDirectoryService
                .RefreshGitOriginAsync(workDirectoryId, cancellationToken)
                .ConfigureAwait(false);

            registeredPath = workDirectory.Value.AbsolutePath;
            workPath = registeredPath;
            workDirectoryName = workDirectory.Value.Name;
            if (!string.IsNullOrWhiteSpace(persisted.WorktreeAbsolutePath))
            {
                if (Directory.Exists(persisted.WorktreeAbsolutePath))
                {
                    workPath = persisted.WorktreeAbsolutePath;
                }
                else
                {
                    var clear = await _sessions.UpdateSessionMetaAsync(
                            new DysonSessionMetaUpdate
                            {
                                SessionId = sessionId,
                                UpdateWorktreeLocation = true,
                                WorktreeAbsolutePath = null,
                                WorktreeBranch = null,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (clear.IsError)
                        return Result<DysonAgentSessionRuntimeLease, string>.AsError(clear.Error);
                }
            }
        }

        if (provider.Value.OpenAi is not null && string.IsNullOrWhiteSpace(workPath))
        {
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(
                "Session has no work directory; cannot resume OpenAI-compatible session.");
        }

        var builtConfig = await _configBuilder.BuildAsync(
                new DysonUiAgentSessionRuntimeConfigRequest
                {
                    AgentMode = persisted.AgentMode,
                    McpAccessMode = persisted.McpAccessMode,
                    WorkDirectoryId = persisted.WorkDirectoryId,
                    WorkRoot = registeredPath,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (builtConfig.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(builtConfig.Error);

        var configLease = builtConfig.Value;
        DysonAgentSession session;
        if (provider.Value.OpenAi is { } openAi)
        {
            var loaded = await OpenAiCompatibleAgentSession.LoadAsync(
                    _sessions,
                    sessionId,
                    openAi,
                    _http!,
                    workPath!,
                    configLease.Config,
                    models: _models,
                    appendResumeLog: true,
                    usageAnalytics: _usageAnalytics,
                    plans: _plans,
                    workDirectoryName: workDirectoryName,
                    registeredWorkDirectoryAbsolutePath: registeredPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (loaded.IsError)
            {
                await configLease.DisposeAsync().ConfigureAwait(false);
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(loaded.Error);
            }

            session = loaded.Value;
        }
        else
        {
            var loaded = await DemoDysonAgentSession.LoadAsync(
                    _sessions,
                    sessionId,
                    provider.Value.Demo!,
                    configLease.Config,
                    models: _models,
                    appendResumeLog: true,
                    workDirectoryAbsolutePath: workPath,
                    registeredWorkDirectoryAbsolutePath: registeredPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (loaded.IsError)
            {
                await configLease.DisposeAsync().ConfigureAwait(false);
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(loaded.Error);
            }

            session = loaded.Value;
        }

        return Result<DysonAgentSessionRuntimeLease, string>.AsValue(
            new DysonAgentSessionRuntimeLease(session, configLease.DisposeAsync));
    }

    private async Task<Result<ResolvedRuntimeProvider, string>> ResolveProviderAsync(
        Guid? modelSlugId,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        DysonModelSlugEntity? slug = null;
        if (modelSlugId is Guid id)
        {
            var get = await _models.GetSlugAsync(id, cancellationToken).ConfigureAwait(false);
            if (get.IsError)
                return Result<ResolvedRuntimeProvider, string>.AsError(get.Error);

            slug = get.Value;
        }
        else
        {
            var def = await _models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
            if (def.IsError)
                return Result<ResolvedRuntimeProvider, string>.AsError(def.Error);

            slug = def.Value;
        }

        var provider = slug?.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);
        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            if (DysonManagedSources.IsCliProxy(provider?.ManagedSource))
            {
                if (_cliProxy is null)
                {
                    return Result<ResolvedRuntimeProvider, string>.AsError(
                        "CLIProxy host is required for OpenAI-compatible cliproxy sessions.");
                }

                var ensure = await _cliProxy
                    .EnsureRunningAsync(progress: null, cancellationToken)
                    .ConfigureAwait(false);
                if (ensure.IsError)
                    return Result<ResolvedRuntimeProvider, string>.AsError(ensure.Error);
            }

            if (_http is null)
            {
                return Result<ResolvedRuntimeProvider, string>.AsError(
                    "HTTP client is required for OpenAI-compatible sessions.");
            }

            return Result<ResolvedRuntimeProvider, string>.AsValue(
                new ResolvedRuntimeProvider(null, new OpenAiCompatibleAgentProvider(slug, reasoningEffort)));
        }

        return Result<ResolvedRuntimeProvider, string>.AsValue(
            new ResolvedRuntimeProvider(new DemoDysonAgentProvider(slug, reasoningEffort), null));
    }

    private sealed record ResolvedRuntimeProvider(
        DemoDysonAgentProvider? Demo,
        OpenAiCompatibleAgentProvider? OpenAi);
}

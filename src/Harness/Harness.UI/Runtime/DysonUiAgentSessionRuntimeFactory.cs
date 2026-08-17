using Harness.UI.Demo;

namespace DysonHarness;

/// <summary>
/// Retained-scope composition factory. <see cref="CreateRootAsync"/> and <see cref="LoadAsync"/>
/// create or resume demo sessions from subject-scoped repositories. OpenAI create/resume stay
/// explicit Result errors until those paths are extracted. Does not resolve circuit/UI services.
/// </summary>
internal sealed class DysonUiAgentSessionRuntimeFactory(
    IDysonSessionRepository sessions,
    IDysonModelRepository models,
    IDysonWorkDirectoryRepository workDirectories,
    DysonUiAgentSessionRuntimeConfigBuilder configBuilder)
    : IDysonAgentSessionRuntimeFactory
{
    private readonly IDysonSessionRepository _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IDysonModelRepository _models =
        models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonWorkDirectoryRepository _workDirectories =
        workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));
    private readonly DysonUiAgentSessionRuntimeConfigBuilder _configBuilder =
        configBuilder ?? throw new ArgumentNullException(nameof(configBuilder));

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

        var provider = await ResolveDemoProviderAsync(
                request.ModelSlugId,
                request.ReasoningEffort,
                cancellationToken,
                openAiError: "Session runtime factory cannot create OpenAI-compatible sessions yet.")
            .ConfigureAwait(false);
        if (provider.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(provider.Error);

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

        var configLease = builtConfig.Value;
        var created = await DemoDysonAgentSession.CreateAsync(
                _sessions,
                provider.Value,
                request.WorkDirectoryId,
                request.AgentMode,
                config: configLease.Config,
                models: _models,
                workDirectoryAbsolutePath: workDirectory.Value.AbsolutePath,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (created.IsError)
        {
            await configLease.DisposeAsync().ConfigureAwait(false);
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(created.Error);
        }

        if (request.MaxTargetContextTokens is int requestedMax)
        {
            var normalized = DysonMaxTargetContextTokens.Normalize(requestedMax);
            created.Value.MaxTargetContextTokens = normalized;
            if (created.Value.PersistenceId != Guid.Empty)
            {
                var persist = await _sessions.UpdateSessionMetaAsync(
                        new DysonSessionMetaUpdate
                        {
                            SessionId = created.Value.PersistenceId,
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
            new DysonAgentSessionRuntimeLease(created.Value, configLease.DisposeAsync));
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
        var provider = await ResolveDemoProviderAsync(
                persisted.ModelSlugId,
                persisted.ReasoningEffort,
                cancellationToken)
            .ConfigureAwait(false);
        if (provider.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(provider.Error);

        string? workPath = null;
        if (persisted.WorkDirectoryId is Guid workDirectoryId && workDirectoryId != Guid.Empty)
        {
            var workDirectory = await _workDirectories
                .GetAsync(workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (workDirectory.IsError)
                return Result<DysonAgentSessionRuntimeLease, string>.AsError(workDirectory.Error);

            workPath = workDirectory.Value.AbsolutePath;
        }

        var builtConfig = await _configBuilder.BuildAsync(
                new DysonUiAgentSessionRuntimeConfigRequest
                {
                    AgentMode = persisted.AgentMode,
                    McpAccessMode = persisted.McpAccessMode,
                    WorkDirectoryId = persisted.WorkDirectoryId,
                    WorkRoot = workPath,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (builtConfig.IsError)
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(builtConfig.Error);

        var configLease = builtConfig.Value;
        var loaded = await DemoDysonAgentSession.LoadAsync(
                _sessions,
                sessionId,
                provider.Value,
                configLease.Config,
                models: _models,
                appendResumeLog: true,
                workDirectoryAbsolutePath: workPath,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsError)
        {
            await configLease.DisposeAsync().ConfigureAwait(false);
            return Result<DysonAgentSessionRuntimeLease, string>.AsError(loaded.Error);
        }

        return Result<DysonAgentSessionRuntimeLease, string>.AsValue(
            new DysonAgentSessionRuntimeLease(loaded.Value, configLease.DisposeAsync));
    }

    private async Task<Result<DemoDysonAgentProvider, string>> ResolveDemoProviderAsync(
        Guid? modelSlugId,
        string? reasoningEffort,
        CancellationToken cancellationToken,
        string? openAiError = null)
    {
        DysonModelSlugEntity? slug = null;
        if (modelSlugId is Guid id)
        {
            var get = await _models.GetSlugAsync(id, cancellationToken).ConfigureAwait(false);
            if (get.IsError)
                return Result<DemoDysonAgentProvider, string>.AsError(get.Error);

            slug = get.Value;
        }
        else
        {
            var def = await _models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
            if (def.IsError)
                return Result<DemoDysonAgentProvider, string>.AsError(def.Error);

            slug = def.Value;
        }

        var provider = slug?.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);
        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            return Result<DemoDysonAgentProvider, string>.AsError(
                openAiError ?? "Session runtime factory cannot load OpenAI-compatible sessions yet.");
        }

        return Result<DemoDysonAgentProvider, string>.AsValue(
            new DemoDysonAgentProvider(slug, reasoningEffort));
    }
}

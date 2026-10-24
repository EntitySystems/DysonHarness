namespace DysonHarness;

/// <summary>Ephemeral OpenAI-compatible provider built from a model provider + slug.</summary>
public sealed class OpenAiCompatibleAgentProvider : DysonAgentProvider
{
    public OpenAiCompatibleAgentProvider(
        DysonModelProviderEntity? provider,
        DysonModelSlugEntity? slug,
        string? reasoningEffort = null)
    {
        ProviderId = provider?.Id ?? slug?.ProviderId;
        SlugId = slug?.Id;
        ApiKey = provider?.ApiKey ?? slug?.Provider?.ApiKey;
        Slug = slug?.Slug ?? "gpt-4o";
        DisplayAlias = slug?.DisplayAlias ?? Slug;
        ProviderKind = provider?.ProviderKind
            ?? slug?.Provider?.ProviderKind
            ?? DysonProviderKinds.OpenAICompatible;
        BaseUrl = provider?.BaseUrl ?? slug?.Provider?.BaseUrl;
        ProviderDisplayName = provider?.DisplayName
            ?? slug?.Provider?.DisplayName
            ?? "OpenAI Compatible";
        OpenAiApiMode = DysonOpenAiApiModes.Normalize(
            provider?.OpenAiApiMode ?? slug?.Provider?.OpenAiApiMode);
        ManagedSource = provider?.ManagedSource ?? slug?.Provider?.ManagedSource;
        ReasoningEffort = NormalizeReasoningEffort(
            reasoningEffort ?? slug?.DefaultReasoningEffort);
        DefaultMaxTargetContextTokens = slug?.DefaultMaxTargetContextTokens;

        if (DysonManagedSources.IsCliProxy(ManagedSource))
        {
            BaseUrl = DysonCliProxyHost.DefaultLocalBaseUrl;
            ApiKey = DysonCliProxyHost.DefaultApiKey;
        }
        else if (DysonManagedSources.IsXaiGrok(ManagedSource))
        {
            // Code-owned URL; ApiKey stays the dyson-xai:<guid> handle that XaiGrokRequestHandler swaps for the token.
            BaseUrl = XaiGrokClientProfile.ChatProxyBaseUrl;
        }
    }

    /// <summary>Convenience: slug must include <see cref="DysonModelSlugEntity.Provider"/>.</summary>
    public OpenAiCompatibleAgentProvider(DysonModelSlugEntity? slug, string? reasoningEffort = null)
        : this(slug?.Provider, slug, reasoningEffort)
    {
    }

    /// <summary>Copy identity from <paramref name="source"/> with an explicit effort (no slug-default fallback).</summary>
    public OpenAiCompatibleAgentProvider WithReasoningEffort(string? reasoningEffort)
    {
        return new OpenAiCompatibleAgentProvider(this, reasoningEffort);
    }

    private OpenAiCompatibleAgentProvider(OpenAiCompatibleAgentProvider source, string? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(source);
        ProviderId = source.ProviderId;
        SlugId = source.SlugId;
        ApiKey = source.ApiKey;
        Slug = source.Slug;
        DisplayAlias = source.DisplayAlias;
        ProviderKind = source.ProviderKind;
        BaseUrl = source.BaseUrl;
        ProviderDisplayName = source.ProviderDisplayName;
        OpenAiApiMode = source.OpenAiApiMode;
        ManagedSource = source.ManagedSource;
        ReasoningEffort = NormalizeReasoningEffort(reasoningEffort);
        DefaultMaxTargetContextTokens = source.DefaultMaxTargetContextTokens;
    }

    public Guid? ProviderId { get; }
    public Guid? SlugId { get; }
    public string? ApiKey { get; }
    public string Slug { get; }
    public string DisplayAlias { get; }
    public string ProviderKind { get; }
    public string? BaseUrl { get; }
    public string ProviderDisplayName { get; }
    public string OpenAiApiMode { get; }
    /// <summary>
    /// When set (e.g. cliproxy-codex, openrouter), provider is managed; null = direct/user-owned.
    /// CLIProxy loopback rewrite applies only when <see cref="DysonManagedSources.IsCliProxy"/>.
    /// </summary>
    public string? ManagedSource { get; }
    /// <summary>
    /// Identity stamped on stored reasoning items (<see cref="DysonAgentTurn.ReasoningItemsSource"/>):
    /// <see cref="ManagedSource"/>, else the base URL's scheme://host:port, else <see cref="ProviderId"/>.
    /// Unmanaged providers all share a null ManagedSource, so the URL keeps their blobs apart.
    /// </summary>
    public string? ReasoningSourceTag =>
        ManagedSource
        ?? (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null)
        ?? ProviderId?.ToString();
    /// <summary>Top-level request reasoning_effort; null/empty = omit.</summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Slug default max target context; null = harness 200K.</summary>
    public int? DefaultMaxTargetContextTokens { get; }

    public static string? NormalizeReasoningEffort(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Read a role model setting (<paramref name="slugKey"/> = slug Guid, <paramref name="effortKey"/> = effort)
    /// from the database. Value null ⇒ unset, malformed, or not OpenAI-compatible (inherit).
    /// Error ⇒ the settings or model read failed.
    /// </summary>
    public static async Task<Result<OpenAiCompatibleAgentProvider?, string>> TryLoadFromSettingAsync(
        IDysonSubjectSettingsRepository settings,
        IDysonModelRepository models,
        string slugKey,
        string effortKey,
        CancellationToken cancellationToken = default)
    {
        var setting = await settings.GetSettingAsync(slugKey, cancellationToken).ConfigureAwait(false);
        if (setting.IsError)
            return Result<OpenAiCompatibleAgentProvider?, string>.AsError(setting.Error);
        if (!Guid.TryParse(setting.Value, out var slugId) || slugId == Guid.Empty)
            return Result<OpenAiCompatibleAgentProvider?, string>.AsValue(null);

        var slug = await models.GetSlugAsync(slugId, cancellationToken).ConfigureAwait(false);
        if (slug.IsError)
            return Result<OpenAiCompatibleAgentProvider?, string>.AsError(slug.Error);

        var provider = slug.Value?.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);
        if (slug.Value is null
            || !string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            return Result<OpenAiCompatibleAgentProvider?, string>.AsValue(null);
        }

        var effort = await settings.GetSettingAsync(effortKey, cancellationToken).ConfigureAwait(false);
        return Result<OpenAiCompatibleAgentProvider?, string>.AsValue(new OpenAiCompatibleAgentProvider(
            slug.Value,
            effort.IsError ? null : NormalizeReasoningEffort(effort.Value)));
    }
}

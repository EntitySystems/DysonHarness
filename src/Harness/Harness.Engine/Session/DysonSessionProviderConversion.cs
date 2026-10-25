namespace DysonHarness;

/// <summary>
/// Persist half of converting a root session to another provider kind (v1: Demo → OpenAI-compatible).
/// Kind is implied by <c>sessions.ModelSlugId</c>, so this rewrites that FK (plus the slug's effort
/// default) and appends one log line. Turns are not touched. The host disposes the live object and
/// reloads the same id; busy checks are the host's job.
/// </summary>
public static class DysonSessionProviderConversion
{
    public static async Task<VoidResult<string>> ConvertSessionProviderAsync(
        IDysonSessionRepository sessions,
        IDysonModelRepository models,
        Guid sessionId,
        Guid targetModelSlugId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(models);

        var full = await sessions.GetFullSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (full.IsError)
            return new VoidResult<string>(full.Error);

        var row = full.Value.Session;
        if (row.ParentSessionId is not null)
            return new VoidResult<string>("Child agents keep their model. Convert the root session instead.");
        if (row.WorkDirectoryId is null)
            return new VoidResult<string>("Session has no work directory; cannot convert it to OpenAI-compatible.");

        var target = await models.GetSlugAsync(targetModelSlugId, cancellationToken).ConfigureAwait(false);
        if (target.IsError)
            return new VoidResult<string>(target.Error);
        if (!target.Value.IsEnabled)
            return new VoidResult<string>("That model slug is disabled. Enable it in Settings → Models first.");
        if (Kind(target.Value) != DysonProviderKinds.OpenAICompatible)
            return new VoidResult<string>("Pick an OpenAI-compatible model.");

        // Null ModelSlugId loads the default slug, so compare against that.
        var current = row.ModelSlugId is Guid currentId
            ? await models.GetSlugAsync(currentId, cancellationToken).ConfigureAwait(false)
            : await models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
        var old = current.IsSuccess ? current.Value : null;
        if (Kind(old) == DysonProviderKinds.OpenAICompatible)
            return new VoidResult<string>("Session is already OpenAI-compatible; pick the model to switch instead.");

        // Keep a session context override; follow the slug default only when it was the old default.
        var followDefault = row.MaxTargetContextTokens is int max && max == old?.DefaultMaxTargetContextTokens;
        var persist = await sessions.UpdateSessionMetaAsync(
            new DysonSessionMetaUpdate
            {
                SessionId = sessionId,
                ModelSlugId = target.Value.Id,
                UpdateReasoningEffort = true,
                ReasoningEffort = target.Value.DefaultReasoningEffort,
                UpdateMaxTargetContextTokens = followDefault,
                MaxTargetContextTokens = followDefault ? target.Value.DefaultMaxTargetContextTokens : null,
            },
            cancellationToken).ConfigureAwait(false);
        if (persist.IsError)
            return persist;

        var log = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.LogLine,
            new DysonSessionLogLogLine($"Converted provider from {old?.Slug ?? DysonProviderKinds.Demo} to {target.Value.Slug}."));
        return await sessions.AppendLogAsync(log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Effective provider kind of a slug; null (no slug) is Demo, same as session load.</summary>
    public static string Kind(DysonModelSlugEntity? slug) =>
        DysonProviderKinds.EffectiveKind(
            slug?.Provider?.ProviderKind ?? DysonProviderKinds.Demo,
            slug?.Provider?.BaseUrl,
            slug?.Provider?.ApiKey);
}

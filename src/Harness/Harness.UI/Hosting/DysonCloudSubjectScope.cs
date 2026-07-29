namespace DysonHarness;

/// <summary>
/// Copies the cloud subject into a child DI scope (e.g. singleton services that
/// <c>CreateAsyncScope</c> for a one-shot repository call).
/// </summary>
public static class DysonCloudSubjectScope
{
    /// <summary>
    /// When the scope has an unset <see cref="DysonScopedSubjectContext"/> and
    /// <paramref name="subjectId"/> is valid, binds it. No-op for Local hosting.
    /// </summary>
    public static void TryBind(IServiceProvider scopeServices, string? subjectId)
    {
        ArgumentNullException.ThrowIfNull(scopeServices);

        if (!DysonSubjectCookieMiddleware.TryValidateSubjectId(subjectId, out var id))
            return;

        if (scopeServices.GetService<DysonScopedSubjectContext>() is not { IsSet: false } cloud)
            return;

        cloud.SetSubjectId(id);
    }
}

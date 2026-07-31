namespace DysonHarness;

/// <summary>
/// Outcome of <see cref="IDysonBrowserControl.ClearBrowserCacheAsync"/>:
/// open agent windows touched and tabs hard-reloaded after HTTP cache clear.
/// </summary>
public sealed class DysonBrowserCacheClearResult
{
    public int Windows { get; init; }
    public int TabsReloaded { get; init; }
}

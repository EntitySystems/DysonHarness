namespace DysonHarness;

public enum ManagedEndpointKind
{
    OpenAiCompatible = 0,
    AnthropicCompatible = 1,
}

/// <param name="BrowserLaunchError">Set when Begin was asked to open the system browser and the OS launch failed; the card link still works.</param>
public sealed record ManagedConnectionBegin(
    string AuthUrl,
    string State,
    string? UserCode = null,
    string? Flow = null,
    int? ExpiresIn = null,
    int? PollIntervalSeconds = null,
    string? BrowserLaunchError = null)
{
    /// <summary>
    /// The one shared browser launch for every managed sign-in (native Codex, native xAI, CLIProxy rows).
    /// <paramref name="open"/> is <see cref="DysonOsLauncher.OpenUrlAsync"/> in production (thread pool, Result, never throws).
    /// A failed launch is recorded on the returned begin instead of being swallowed.
    /// </summary>
    internal async Task<ManagedConnectionBegin> OpenInBrowserAsync(Func<string, Task<VoidResult<string>>> open)
    {
        var opened = await open(AuthUrl).ConfigureAwait(false);
        return opened.IsError ? this with { BrowserLaunchError = opened.Error } : this;
    }
}

public sealed record ManagedConnectionComplete(
    string Status,
    bool IsComplete,
    string? Message = null);

public sealed record ManagedConnectionVerify(
    Guid ProviderId,
    int SlugCount,
    IReadOnlyList<string> Slugs,
    string? Note = null);

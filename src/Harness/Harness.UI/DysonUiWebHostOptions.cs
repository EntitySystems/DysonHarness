namespace Harness.UI;

/// <summary>Optional overrides when embedding the Blazor host (e.g. Windows CEF shell).</summary>
public sealed class DysonUiWebHostOptions
{
    /// <summary>Kestrel bind URLs (e.g. <c>http://127.0.0.1:0</c> for an ephemeral loopback port).</summary>
    public string? Urls { get; init; }

    /// <summary>Skip <c>UseHttpsRedirection</c> (desktop loopback HTTP).</summary>
    public bool SkipHttpsRedirection { get; init; }

    /// <summary>Content root; defaults to the Harness.UI assembly directory when unset.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>Web root; defaults to <c>{ContentRoot}/wwwroot</c> when unset.</summary>
    public string? WebRoot { get; init; }
}

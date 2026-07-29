namespace DysonHarness;

/// <summary>
/// Bindable hosting options from configuration section <see cref="SectionName"/>
/// (e.g. <c>"DysonHosting": { "Mode": "Local" }</c>).
/// </summary>
public sealed class DysonHostingOptions
{
    /// <summary>Configuration section name: <c>DysonHosting</c>.</summary>
    public const string SectionName = "DysonHosting";

    /// <summary>Hosting mode; defaults to <see cref="DysonHostingMode.Local"/>.</summary>
    public DysonHostingMode Mode { get; set; } = DysonHostingMode.Local;
}

using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>
/// Holds one iframe <c>srcdoc</c> per visualization revision. <see cref="DysonHtmlVisualizationDocument.Create"/>
/// mints a fresh nonce each call, so a new string would reload the iframe; this calls it only when the id or content changes.
/// </summary>
public sealed class DysonHtmlVisualizationSrcdocCache
{
    private DysonHtmlVisualizationRevision? _applied;

    public string Document { get; private set; } = "";

    /// <summary>True when a new document was built. False keeps the previous string.</summary>
    public bool TryUpdate(DysonHtmlVisualization visualization)
    {
        ArgumentNullException.ThrowIfNull(visualization);
        if (!DysonHtmlVisualizationIdentity.NeedsUpdate(_applied, visualization))
            return false;
        _applied = DysonHtmlVisualizationIdentity.Capture(visualization);
        Document = DysonHtmlVisualizationDocument.Create(visualization);
        return true;
    }
}

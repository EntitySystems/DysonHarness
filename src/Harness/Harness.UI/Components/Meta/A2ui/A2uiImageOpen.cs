namespace Harness.UI.Components.Meta.A2ui;

/// <summary>
/// One slide of the large image view. <see cref="PreviewUrl"/> is the surface's own same-origin preview URL (path slides),
/// <see cref="RemoteUrl"/> an http(s) URL that passed the host rule (loads only after the user consented), <see cref="OpenFunc"/>
/// the file-viewer func (path slides), <see cref="Note"/> the text shown when there is nothing to display. <see cref="Title"/>
/// is already normalised by the Engine (the band text).
/// </summary>
public sealed record A2uiImageSlide(
    string Alt,
    string? Title,
    string Caption,
    string? PreviewUrl,
    string? RemoteUrl,
    string? OpenFunc,
    string? Note);

/// <summary>An opener's slide plus the loaded card of a path slide; the surface registers the preview id and fills PreviewUrl/OpenFunc.</summary>
public sealed record A2uiSlideSource(A2uiImageSlide Slide, DysonHarness.DysonA2uiImageCard? Card);

/// <summary>
/// What the surface's large view shows: node id (closes when the node goes), a header title (blank: the current slide's title or
/// alt), the slides, the first index, and whether the inline gallery already had the user's remote-image consent.
/// </summary>
public sealed record A2uiImageOpen(
    string NodeId,
    string Title,
    IReadOnlyList<A2uiImageSlide> Slides,
    int Index,
    bool RemoteConsented = false);

/// <summary>Presentation text for the remote-image consent button (inline gallery and modal).</summary>
internal static class A2uiRemoteImages
{
    public static string Label(int count, IReadOnlyList<string> hosts)
    {
        var noun = count == 1 ? "remote image" : "remote images";
        if (hosts.Count == 0)
            return $"Load {count} {noun}";
        var more = hosts.Count > 1 ? $", +{hosts.Count - 1} {(hosts.Count == 2 ? "host" : "hosts")}" : "";
        return $"Load {count} {noun} ({hosts[0]}{more})";
    }
}

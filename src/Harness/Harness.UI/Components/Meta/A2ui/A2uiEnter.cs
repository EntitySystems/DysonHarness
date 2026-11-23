using System.Globalization;
using DysonHarness;

namespace Harness.UI.Components.Meta.A2ui;

/// <summary>
/// First-appearance schedule of one live surface. Each component gets a CSS animation delay in document order
/// (depth-first), 350 ms apart; a component already scheduled keeps its delay and never replays, and a component
/// added by a later revision starts after the last one already scheduled. Pure bookkeeping: the animation itself is
/// CSS (<c>.a2ui-enter</c> in A2uiNode.razor.css), the delay reaches it as a custom property.
/// </summary>
internal sealed class A2uiEnter
{
    internal const int StepMs = 350;

    // ponytail: ceiling of 40 staggered components per surface; everything past it appears with the 40th so a
    // huge surface cannot take minutes. Upgrade path: shrink the step as the count grows.
    internal const int MaxStaggered = 40;

    private readonly Dictionary<string, int> _delays = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _slots;
    private long _nextAt;

    /// <summary>
    /// Slots every not-yet-seen component reachable from <c>root</c>. Not walked, so they show at once: the
    /// content of Button/Pressable (inline), Tabs, Accordion, Carousel, Modal and Popover (hidden until used).
    /// Table sections, rows and cells take no slot: they cannot sit in a wrapper, the table appears as one.
    /// Components that share an id (template items) share one slot.
    /// </summary>
    internal void Schedule(DysonA2uiSurface surface, long nowMs)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit("root");

        void Visit(string id)
        {
            if (!visited.Add(id) || !surface.Components.TryGetValue(id, out var component))
                return;

            var name = DysonA2uiSurfaces.Name(component);
            if (name is not ("TableSection" or "TableRow" or "TableCell") && _seen.Add(id))
            {
                var start = Math.Max(nowMs, _nextAt);
                if (_slots++ < MaxStaggered)
                    _nextAt = start + StepMs;
                else
                    start = Math.Max(nowMs, _nextAt - StepMs);
                _delays[id] = (int)(start - nowMs);
            }

            if (name is "Button" or "Pressable" or "Tabs" or "Accordion" or "Carousel" or "Modal" or "Popover")
                return;
            foreach (var (childId, _) in DysonA2uiSurfaces.Children(component))
                Visit(childId);
        }
    }

    /// <summary>
    /// Inline style for the node's wrapper, or null for "no entrance". An id nobody scheduled (rendered first
    /// later, e.g. a tab opened afterwards) is marked seen so it never gains a wrapper, and loses its state, later.
    /// </summary>
    internal string? Style(string? id)
    {
        if (id is null)
            return null;
        if (_delays.TryGetValue(id, out var ms))
            return string.Create(CultureInfo.InvariantCulture, $"--a2ui-enter-delay: {ms}ms");
        _seen.Add(id);
        return null;
    }
}

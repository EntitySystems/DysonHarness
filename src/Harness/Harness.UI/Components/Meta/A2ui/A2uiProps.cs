using System.Globalization;
using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.UI.Components.Meta.A2ui;

/// <summary>
/// Read-only prop helpers shared by A2uiNode and the group components. Render only: every resolve and
/// expansion is <see cref="DysonA2uiBinding"/>. Nothing here returns an agent string for an attribute
/// except the aria label/description, which Blazor escapes.
/// </summary>
internal static class A2uiProps
{
    /// <summary>Literal string, else null (not a binding).</summary>
    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Literal integral number, else null.</summary>
    public static int? Int(JsonNode? node)
    {
        if (Double(node) is not { } d || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
            return null;
        return (int)d;
    }

    /// <summary>Literal finite number, else null.</summary>
    public static double? Double(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>Literal bool, else null.</summary>
    public static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>String literal or binding; "" when unresolved.</summary>
    public static string String(A2uiSurfaceView view, JsonNode? dyn, string scope) =>
        DysonA2uiBinding.ResolveString(dyn, view.Model, scope);

    /// <summary>Number literal or binding; null when unresolved, not a number, or not finite.</summary>
    public static double? Number(A2uiSurfaceView view, JsonNode? dyn, string scope) =>
        Double(DysonA2uiBinding.ResolveValue(dyn, view.Model, scope));

    /// <summary>Expanded child ids (static list or template) under <paramref name="key"/>.</summary>
    public static IReadOnlyList<(string ComponentId, string Scope)> Children(A2uiSurfaceView view, JsonObject c, string scope, string key = "children") =>
        c[key] is { } children ? DysonA2uiBinding.ExpandChildren(children, view.Model, scope) : [];

    /// <summary>Validated number on a direct Row/Column child, formatted invariant. Never an agent string.</summary>
    public static string? Weight(A2uiSurfaceView view, string childId)
    {
        if (view.Component(childId)?["weight"] is not JsonValue v || !v.TryGetValue<double>(out var weight))
            return null;
        if (double.IsNaN(weight) || weight <= 0)
            return null;
        return Math.Min(weight, 1000).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>@accessibility</c> → aria attributes. <paramref name="description"/> is the text for a sr-only
    /// span with id <c>{domId}-desc</c> (null when there is none).
    /// </summary>
    public static Dictionary<string, object> Aria(A2uiSurfaceView view, JsonObject c, string scope, string domId, out string? description)
    {
        description = null;
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
        if (c["@accessibility"] is not JsonObject a)
            return attributes;

        if (a.ContainsKey("label"))
            attributes["aria-label"] = String(view, a["label"], scope);
        if (a.ContainsKey("description"))
        {
            description = String(view, a["description"], scope);
            attributes["aria-describedby"] = $"{domId}-desc";
        }

        var live = Str(a["live"]) switch
        {
            "polite" => "polite",
            "assertive" => "assertive",
            "off" => "off",
            _ => null,
        };
        if (live is not null)
            attributes["aria-live"] = live;
        if (a.ContainsKey("hidden") && DysonA2uiBinding.ResolveBool(a["hidden"], view.Model, scope))
            attributes["aria-hidden"] = "true";
        return attributes;
    }

    /// <summary><c>gap</c> int 0..12 → fixed class <c>a2ui-gap--N</c>; anything else "".</summary>
    public static string GapClass(JsonObject c) => Step(c["gap"], "a2ui-gap--");

    /// <summary><c>padding</c> int 0..12 → fixed class <c>a2ui-pad--N</c>; anything else "".</summary>
    public static string PadClass(JsonObject c) => Step(c["padding"], "a2ui-pad--");

    private static string Step(JsonNode? node, string prefix) =>
        Int(node) is { } n && n is >= 0 and <= 12 ? prefix + n.ToString(CultureInfo.InvariantCulture) : "";
}

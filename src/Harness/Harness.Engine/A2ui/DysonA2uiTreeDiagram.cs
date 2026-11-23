using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary><see cref="Color"/> is the fixed word red, green or accent (a fixed class in the UI).</summary>
public sealed record DysonA2uiTreeTag(string Label, string Color);

/// <summary>One drawn card. <see cref="Left"/> / <see cref="Top"/> are invariant pixel numbers; <see cref="Level"/> is the 0-based depth.</summary>
public sealed record DysonA2uiTreeCard(
    string Id, int Level, string Title, string? Text, IReadOnlyList<DysonA2uiTreeTag> Tags, string Left, string Top);

/// <summary>
/// Everything the TreeDiagram component draws: <see cref="Message"/> ("Empty tree.") replaces the diagram when set.
/// <see cref="Links"/> are SVG path <c>d</c> strings in the same pixel space as <see cref="Cards"/>.
/// </summary>
public sealed record DysonA2uiTreeLayout(
    string? Message, string? Title, bool Horizontal, int Width, int Height,
    IReadOnlyList<DysonA2uiTreeCard> Cards, IReadOnlyList<string> Links)
{
    public string ViewBox => $"0 0 {Width} {Height}";
}

/// <summary>
/// Dyson-catalog <c>TreeDiagram</c> (<c>nodes</c> nested <c>{id, title, text?, tags?, children?}</c>, <c>flow</c> vertical|horizontal,
/// <c>title</c>): <see cref="Check"/> is the validator's tree rule (the catalog already checks every level's shape) and
/// <see cref="Build"/> is the pure layout. Every number is formatted here, so the UI emits only fixed classes and Engine numbers.
/// </summary>
public static class DysonA2uiTreeDiagram
{
    public const string ComponentName = "TreeDiagram";
    public const int MaxNodes = 100;
    public const int MaxDepth = 12;
    public const int MaxTags = 6;
    public const int CardWidth = 200;
    public const int CardHeight = 112;

    /// <summary>Between depth levels (along the flow).</summary>
    public const int GapMain = 40;

    /// <summary>Between siblings (across the flow).</summary>
    public const int GapCross = 16;

    public const string EmptyMessage = "Empty tree.";
    private const string BindingMessage = "TreeDiagram nodes, children and tags must be literal arrays.";

    /// <summary>
    /// Rules the catalog cannot express: literal arrays only, unique ids, at most <see cref="MaxNodes"/> nodes and
    /// <see cref="MaxDepth"/> levels. Returns the pointer suffix under the component and the message, or null.
    /// A nested JSON value cannot hold a cycle or a missing parent, so those need no check.
    /// </summary>
    public static (string Suffix, string Message)? Check(JsonNode? nodes) =>
        nodes is JsonArray roots ? CheckLevel(roots, "/nodes", 1, new HashSet<string>(StringComparer.Ordinal)) : ("/nodes", BindingMessage);

    private static (string, string)? CheckLevel(JsonArray list, string pointer, int level, HashSet<string> ids)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var at = $"{pointer}/{i}";
            if (list[i] is not JsonObject node)
                return (at, "expected an object.");
            if (level > MaxDepth)
                return (at, $"a tree is at most {MaxDepth} levels deep.");
            if (ids.Count >= MaxNodes)
                return (at, $"a tree has at most {MaxNodes} nodes.");
            var id = DysonA2uiValidator.AsString(node["id"]);
            if (id is null || !ids.Add(id))
                return ($"{at}/id", $"duplicate node id '{id}'.");
            if (node["tags"] is { } tags && tags is not JsonArray)
                return ($"{at}/tags", BindingMessage);
            if (node["children"] is not { } children)
                continue;
            if (children is not JsonArray kids)
                return ($"{at}/children", BindingMessage);
            if (CheckLevel(kids, $"{at}/children", level + 1, ids) is { } e)
                return e;
        }

        return null;
    }

    private sealed class Flat(string id, int level, string title, string? text, IReadOnlyList<DysonA2uiTreeTag> tags)
    {
        public readonly string Id = id;
        public readonly int Level = level;
        public readonly string Title = title;
        public readonly string? Text = text;
        public readonly IReadOnlyList<DysonA2uiTreeTag> Tags = tags;
        public int Parent = -1;
        public int FirstChild = -1;
        public int LastChild = -1;
        public double Slot;
    }

    /// <summary>
    /// Component JSON to layout. Never throws: malformed or over-cap input is cut short (the validator rejects it earlier).
    /// ponytail: one slot per leaf, parents centred over children: O(n) and siblings never overlap, but an unbalanced tree wastes
    /// width. Upgrade: Reingold-Tilford/Buchheim contour packing.
    /// ponytail: fixed 200x112 card: long titles ellipsize and text clamps to 2 lines (the full text stays in the DOM for screen
    /// readers), so labels never overlap a neighbour. Upgrade: measure card heights client-side and lay out per row.
    /// ponytail: node cap 100, depth 12: a 100-leaf vertical tree is ~21,600px wide and scrolls inside the card. Upgrade: collapse
    /// subtrees, or pick the flow from the tree's shape.
    /// </summary>
    public static DysonA2uiTreeLayout Build(JsonObject component, JsonNode? model = null, string scope = "")
    {
        var resolved = DysonA2uiBinding.ResolveString(component["title"], model ?? new JsonObject(), scope);
        string? title = string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        var horizontal = DysonA2uiValidator.AsString(component["flow"]) == "horizontal";

        var flat = new List<Flat>();
        if (component["nodes"] is JsonArray roots)
            Visit(roots, 0, -1, flat);
        if (flat.Count == 0)
            return new DysonA2uiTreeLayout(EmptyMessage, title, horizontal, 0, 0, [], []);

        // Leaves take the next slot in preorder (left to right); children always come after their parent, so a reverse walk
        // sees every child's slot before its parent's, which sits at the midpoint of its first and last child.
        var next = 0;
        foreach (var n in flat.Where(n => n.FirstChild < 0))
            n.Slot = next++;
        for (var i = flat.Count - 1; i >= 0; i--)
        {
            if (flat[i].FirstChild >= 0)
                flat[i].Slot = (flat[flat[i].FirstChild].Slot + flat[flat[i].LastChild].Slot) / 2;
        }

        var xs = new double[flat.Count];
        var ys = new double[flat.Count];
        for (var i = 0; i < flat.Count; i++)
        {
            var (level, slot) = (flat[i].Level, flat[i].Slot);
            double along = level * (horizontal ? CardWidth + GapMain : CardHeight + GapMain);
            var across = slot * (horizontal ? CardHeight + GapCross : CardWidth + GapCross);
            (xs[i], ys[i]) = horizontal ? (along, across) : (across, along);
        }

        var cards = new List<DysonA2uiTreeCard>(flat.Count);
        var links = new List<string>();
        for (var i = 0; i < flat.Count; i++)
        {
            var n = flat[i];
            cards.Add(new DysonA2uiTreeCard(n.Id, n.Level, n.Title, n.Text, n.Tags, F(xs[i]), F(ys[i])));
            if (n.Parent < 0)
                continue;
            var p = n.Parent;
            links.Add(horizontal
                ? $"M{F(xs[p] + CardWidth)},{F(ys[p] + CardHeight / 2.0)} H{F(xs[p] + CardWidth + GapMain / 2.0)} V{F(ys[i] + CardHeight / 2.0)} H{F(xs[i])}"
                : $"M{F(xs[p] + CardWidth / 2.0)},{F(ys[p] + CardHeight)} V{F(ys[p] + CardHeight + GapMain / 2.0)} H{F(xs[i] + CardWidth / 2.0)} V{F(ys[i])}");
        }

        var width = (int)Math.Ceiling(xs.Max()) + CardWidth;
        var height = (int)Math.Ceiling(ys.Max()) + CardHeight;
        return new DysonA2uiTreeLayout(null, title, horizontal, width, height, cards, links);
    }

    private static string F(double v) => DysonA2uiChartGeometry.F(v);

    private static void Visit(JsonArray list, int level, int parent, List<Flat> flat)
    {
        if (level >= MaxDepth)
            return;
        foreach (var item in list)
        {
            if (flat.Count >= MaxNodes)
                return;
            if (item is not JsonObject node)
                continue;
            var tags = new List<DysonA2uiTreeTag>();
            if (node["tags"] is JsonArray tagList)
            {
                foreach (var t in tagList.OfType<JsonObject>().Take(MaxTags))
                {
                    var color = DysonA2uiValidator.AsString(t["color"]);
                    tags.Add(new DysonA2uiTreeTag(
                        DysonA2uiValidator.AsString(t["label"]) ?? "",
                        color is "red" or "green" ? color : "accent"));
                }
            }

            var text = DysonA2uiValidator.AsString(node["text"]);
            var index = flat.Count;
            flat.Add(new Flat(
                DysonA2uiValidator.AsString(node["id"]) ?? "", level, DysonA2uiValidator.AsString(node["title"]) ?? "",
                string.IsNullOrWhiteSpace(text) ? null : text, tags) { Parent = parent });
            if (parent >= 0)
            {
                var p = flat[parent];
                p.FirstChild = p.FirstChild < 0 ? index : p.FirstChild;
                p.LastChild = index;
            }

            if (node["children"] is JsonArray kids)
                Visit(kids, level + 1, index, flat);
        }
    }
}

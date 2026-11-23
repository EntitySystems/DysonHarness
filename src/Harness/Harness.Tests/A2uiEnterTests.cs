using System.Text.Json.Nodes;
using DysonHarness;
using Harness.UI.Components.Meta.A2ui;
using Xunit;

namespace Harness.Tests;

public sealed class A2uiEnterTests
{
    private static DysonA2uiSurface Surface(params (string Id, string Component, string[] Children)[] nodes)
    {
        var components = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (id, component, children) in nodes)
        {
            var o = new JsonObject { ["id"] = id, ["component"] = component };
            if (children.Length > 0)
                o["children"] = new JsonArray(children.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
            components[id] = o;
        }

        return new DysonA2uiSurface("s", null, components, [], 1, null);
    }

    private static string Delay(int ms) => $"--a2ui-enter-delay: {ms}ms";

    [Fact]
    public void Depth_first_order_350ms_apart()
    {
        var enter = new A2uiEnter();
        enter.Schedule(Surface(
            ("root", "Column", ["card", "last"]),
            ("card", "Card", ["inner"]),
            ("inner", "Text", []),
            ("last", "Text", [])), nowMs: 1000);

        Assert.Equal(Delay(0), enter.Style("root"));
        Assert.Equal(Delay(350), enter.Style("card"));
        Assert.Equal(Delay(700), enter.Style("inner"));
        Assert.Equal(Delay(1050), enter.Style("last"));
    }

    [Fact]
    public void Update_keeps_old_delays_and_continues_after_the_last_scheduled()
    {
        var enter = new A2uiEnter();
        enter.Schedule(Surface(("root", "Column", ["a"]), ("a", "Text", [])), nowMs: 0);

        // Shown long ago: the new one starts now, not at the old cadence and not at zero ahead of it.
        enter.Schedule(Surface(("root", "Column", ["a", "b"]), ("a", "Text", []), ("b", "Text", [])), nowMs: 10_000);
        Assert.Equal(Delay(350), enter.Style("a"));
        Assert.Equal(Delay(0), enter.Style("b"));

        // Still queued: the next one follows the last scheduled.
        var queued = new A2uiEnter();
        queued.Schedule(Surface(("root", "Column", ["a"]), ("a", "Text", [])), nowMs: 0);
        queued.Schedule(Surface(("root", "Column", ["a", "b"]), ("a", "Text", []), ("b", "Text", [])), nowMs: 100);
        Assert.Equal(Delay(600), queued.Style("b"));
    }

    [Fact]
    public void Stagger_stops_at_forty_components()
    {
        var ids = Enumerable.Range(0, 60).Select(i => $"t{i}").ToArray();
        var nodes = new List<(string, string, string[])> { ("root", "Column", ids) };
        nodes.AddRange(ids.Select(id => (id, "Text", Array.Empty<string>())));
        var enter = new A2uiEnter();
        enter.Schedule(Surface([.. nodes]), nowMs: 0);

        // root + t0..t38 take slots 0..39; everything after appears with the 40th.
        Assert.Equal(Delay(39 * A2uiEnter.StepMs), enter.Style("t38"));
        Assert.Equal(Delay(39 * A2uiEnter.StepMs), enter.Style("t59"));
    }

    [Fact]
    public void Unscheduled_ids_and_table_parts_get_no_wrapper()
    {
        var enter = new A2uiEnter();
        enter.Schedule(Surface(
            ("root", "Table", ["row"]),
            ("row", "TableRow", ["cell"]),
            ("cell", "TableCell", [])), nowMs: 0);

        Assert.NotNull(enter.Style("root"));
        Assert.Null(enter.Style("row"));
        Assert.Null(enter.Style("cell"));
        Assert.Null(enter.Style("never-scheduled"));
        Assert.Null(enter.Style(null));
    }
}

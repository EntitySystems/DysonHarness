using System.Text;
using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Plan 374: TreeDiagram layout (both flows), validation (colors, flows, ids, bindings, caps) and Dyson-only catalog.</summary>
public class DysonA2uiTreeDiagramTests
{
    private const string P0 = "/messages/0/createSurface/components/0";
    private const string TwoLeaves =
        """[{"id":"r","title":"Root","children":[{"id":"a","title":"A"},{"id":"b","title":"B"}]}]""";

    private static JsonObject Component(string nodes, string extra = "") =>
        JsonNode.Parse($$"""{"id":"t","component":"TreeDiagram","nodes":{{nodes}}{{extra}}}""")!.AsObject();

    private static Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> Parse(string props, string catalogExtra = "") =>
        DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create(
            "{\"id\":\"root\",\"component\":\"TreeDiagram\"," + props + "}", catalogExtra, "s1"));

    private static void AssertFails(Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> result, string path, string? message = null)
    {
        Assert.True(result.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.Equal(path, result.Error.Path);
        if (message is not null)
            Assert.Equal(message, result.Error.Message);
    }

    private static string Chain(int depth)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < depth; i++)
            sb.Append("{\"id\":\"n").Append(i).Append("\",\"title\":\"N\",\"children\":[");
        for (var i = 0; i < depth; i++)
            sb.Append("]}");
        return "[" + sb + "]";
    }

    [Fact]
    public void Layout_places_parents_over_children_and_flow_swaps_the_axes()
    {
        var vertical = DysonA2uiTreeDiagram.Build(Component(TwoLeaves));
        Assert.False(vertical.Horizontal);
        Assert.Null(vertical.Message);
        Assert.Equal(
            [("r", "108", "0"), ("a", "0", "152"), ("b", "216", "152")],
            vertical.Cards.Select(c => (c.Id, c.Left, c.Top)).ToArray());
        Assert.Equal(2, vertical.Links.Count);
        Assert.Equal((416, 264), (vertical.Width, vertical.Height));
        Assert.Equal("M208,112 V132 H100 V152", vertical.Links[0]);

        var horizontal = DysonA2uiTreeDiagram.Build(Component(TwoLeaves, ",\"flow\":\"horizontal\""));
        Assert.True(horizontal.Horizontal);
        Assert.Equal(
            [("r", "0", "64"), ("a", "240", "0"), ("b", "240", "128")],
            horizontal.Cards.Select(c => (c.Id, c.Left, c.Top)).ToArray());
        Assert.Equal(2, horizontal.Links.Count);
        Assert.Equal((440, 240), (horizontal.Width, horizontal.Height));
        Assert.Equal("M200,120 H220 V56 H240", horizontal.Links[0]);

        var solo = DysonA2uiTreeDiagram.Build(Component("""[{"id":"a","title":"Solo"}]"""));
        Assert.Single(solo.Cards);
        Assert.Empty(solo.Links);

        var empty = DysonA2uiTreeDiagram.Build(Component("[]", ",\"title\":\"T\""));
        Assert.Equal("Empty tree.", empty.Message);
        Assert.Equal("T", empty.Title);
        Assert.Empty(empty.Cards);
    }

    [Fact]
    public void Layout_centres_uneven_subtrees_keeps_tags_and_lays_out_several_roots_as_siblings()
    {
        var layout = DysonA2uiTreeDiagram.Build(Component(
            """
            [{"id":"r","title":"R","text":"t","tags":[{"label":"x","color":"red"},{"label":"y"},{"label":"z","color":"weird"}],
              "children":[{"id":"a","title":"A","children":[{"id":"a1","title":"A1"},{"id":"a2","title":"A2"}]},{"id":"b","title":"B"}]},
             {"id":"r2","title":"R2"}]
            """));
        var byId = layout.Cards.ToDictionary(c => c.Id);
        // Leaves a1, a2, b, r2 take slots 0..3; a sits over a1/a2 (0.5), r over a and b (1.25).
        Assert.Equal("108", byId["a"].Left);
        Assert.Equal("270", byId["r"].Left);
        Assert.Equal("648", byId["r2"].Left);
        Assert.Equal(["red", "accent", "accent"], byId["r"].Tags.Select(t => t.Color));
        Assert.Equal(2, byId["a1"].Level);
        Assert.Equal(4, layout.Links.Count);
    }

    [Fact]
    public void Validation_accepts_only_the_three_colors_and_two_flows()
    {
        foreach (var color in new[] { "red", "green", "accent" })
        {
            Assert.True(Parse($$"""
                "nodes":[{"id":"a","title":"A","tags":[{"label":"l","color":"{{color}}"}]}]
                """).IsSuccess);
        }

        Assert.True(Parse("""
            "nodes":[{"id":"a","title":"A","tags":[{"label":"l"}]}],"flow":"horizontal","title":"T"
            """).IsSuccess);
        Assert.True(Parse("\"nodes\":[]").IsSuccess);

        foreach (var color in new[] { "blue", "#f00" })
        {
            AssertFails(
                Parse($$"""
                    "nodes":[{"id":"a","title":"A","tags":[{"label":"l","color":"{{color}}"}]}]
                    """),
                P0 + "/nodes/0/tags/0/color",
                "must be one of: red, green, accent.");
        }

        AssertFails(Parse("\"nodes\":[],\"flow\":\"diagonal\""), P0 + "/flow", "must be one of: vertical, horizontal.");
        AssertFails(Parse("\"flow\":\"vertical\""), P0 + "/nodes", "TreeDiagram.nodes is required.");
        AssertFails(Parse("\"nodes\":[{\"id\":\"a\"}]"), P0 + "/nodes/0/title");
        AssertFails(Parse("\"nodes\":[{\"title\":\"a\"}]"), P0 + "/nodes/0/id");
        AssertFails(Parse("\"nodes\":[{\"id\":\"a\",\"title\":\"A\",\"x\":1}]"), P0 + "/nodes/0/x");
        AssertFails(Parse("\"nodes\":[{\"id\":\"a\",\"title\":{\"@path\":\"/t\"}}]"), P0 + "/nodes/0/title");
        AssertFails(Parse("\"nodes\":[{\"id\":\"a\",\"title\":\"A\",\"text\":{\"@path\":\"/t\"}}]"), P0 + "/nodes/0/text");
        AssertFails(Parse("\"nodes\":[{\"id\":\"a\",\"title\":\"A\",\"tags\":[{\"label\":{\"@path\":\"/t\"}}]}]"), P0 + "/nodes/0/tags/0/label");

        var sevenTags = string.Join(',', Enumerable.Range(0, 7).Select(i => $$"""{"label":"t{{i}}"}"""));
        AssertFails(Parse($$"""
            "nodes":[{"id":"a","title":"A","tags":[{{sevenTags}}]}]
            """), P0 + "/nodes/0/tags/6");

        // Dyson catalog only.
        AssertFails(Parse("\"nodes\":[]", ",\"catalogId\":\"" + DysonA2uiProtocol.BasicCatalogId + "\""), P0 + "/component");
    }

    [Fact]
    public void Validation_rejects_duplicate_ids_bindings_and_the_node_and_depth_caps()
    {
        AssertFails(
            Parse("""
                "nodes":[{"id":"api","title":"A","children":[{"id":"auth","title":"x"},{"id":"auth","title":"y"}]}]
                """),
            P0 + "/nodes/0/children/1/id",
            "duplicate node id 'auth'.");
        AssertFails(Parse("""
            "nodes":[{"id":"a","title":"A"},{"id":"a","title":"B"}]
            """), P0 + "/nodes/1/id");

        AssertFails(Parse("\"nodes\":{\"@path\":\"/n\"}"), P0 + "/nodes", "TreeDiagram nodes, children and tags must be literal arrays.");
        AssertFails(Parse("\"nodes\":[{\"id\":\"a\",\"title\":\"A\",\"children\":{\"@path\":\"/n\"}}]"), P0 + "/nodes/0/children");
        AssertFails(Parse("\"nodes\":[{\"id\":\"a\",\"title\":\"A\",\"tags\":{\"@path\":\"/n\"}}]"), P0 + "/nodes/0/tags");

        static string Flat(int count)
        {
            var kids = string.Join(',', Enumerable.Range(1, count - 1).Select(i => "{\"id\":\"n" + i + "\",\"title\":\"N\"}"));
            return "[{\"id\":\"root\",\"title\":\"R\",\"children\":[" + kids + "]}]";
        }

        Assert.True(Parse("\"nodes\":" + Flat(100)).IsSuccess);
        AssertFails(Parse("\"nodes\":" + Flat(101)), P0 + "/nodes/0/children/99", "a tree has at most 100 nodes.");

        Assert.True(Parse("\"nodes\":" + Chain(12)).IsSuccess);
        var deep = Parse("\"nodes\":" + Chain(13));
        Assert.True(deep.IsError);
        Assert.Equal(P0 + "/nodes/0" + string.Concat(Enumerable.Repeat("/children/0", 12)), deep.Error.Path);
        Assert.Equal("a tree is at most 12 levels deep.", deep.Error.Message);
    }
}

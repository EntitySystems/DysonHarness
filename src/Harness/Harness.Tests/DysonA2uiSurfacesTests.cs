using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Pure reducer, tree check, serializer and binding helpers.</summary>
public class DysonA2uiSurfacesTests
{
    private static IReadOnlyList<DysonA2uiMessage> Parse(string json)
    {
        var result = DysonA2uiValidator.Parse(json);
        Assert.True(result.IsSuccess, result.IsError ? result.Error.ToWireJson() : null);
        return result.Value;
    }

    private static DysonA2uiSurface Created(string components, Guid? questionId = null, string extra = "")
    {
        var applied = DysonA2uiSurfaces.Apply(null, questionId, Parse(DysonA2uiValidatorTests.Create(components, extra, "s1")));
        Assert.True(applied.IsSuccess, applied.IsError ? applied.Error.ToWireJson() : null);
        return applied.Value!;
    }

    [Fact]
    public void Second_message_same_surfaceId_updates_existing_surface()
    {
        var first = Created("""{"id":"root","component":"Text","text":"old"}""");
        var update = Parse("""
            [{"version":"1.0","updateComponents":{"surfaceId":"s1","components":[{"id":"root","component":"Text","text":{"@path":"/msg"}}]}},
             {"version":"1.0","updateDataModel":{"surfaceId":"s1","path":"/msg","value":"new"}}]
            """);

        var second = DysonA2uiSurfaces.Apply(first, null, update);

        Assert.True(second.IsSuccess);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Value!.Revision);
        Assert.Equal("old", (string?)first.Components["root"]["text"]);
        Assert.Equal("new", DysonA2uiBinding.ResolveString(second.Value.Components["root"]["text"], second.Value.DataModel, ""));
    }

    [Fact]
    public void createSurface_twice_returns_INTEGRITY_ERROR_and_keeps_first_surface()
    {
        var first = Created("""{"id":"root","component":"Text","text":"one"}""");
        var again = DysonA2uiSurfaces.Apply(first, null, Parse(DysonA2uiValidatorTests.Create("""{"id":"root","component":"Text","text":"two"}""", surfaceId: "s1")));

        Assert.True(again.IsError);
        Assert.Equal(DysonA2uiProtocol.IntegrityError, again.Error.Code);
        Assert.Equal("s1", again.Error.SurfaceId);
        Assert.Equal("one", (string?)first.Components["root"]["text"]);

        var stub = new DysonA2uiSurface("s1", null, new Dictionary<string, JsonObject>(), [], 0, "bad");
        var retry = DysonA2uiSurfaces.Apply(stub, null, Parse(DysonA2uiValidatorTests.Create("""{"id":"root","component":"Text","text":"two"}""", surfaceId: "s1")));
        Assert.True(retry.IsSuccess);
        Assert.Null(retry.Value!.LastError);
        Assert.Equal(1, retry.Value.Revision);
    }

    [Fact]
    public void Deep_updateDataModel_pointer_is_VALIDATION_FAILED_not_a_throw()
    {
        var first = Created("""{"id":"root","component":"Text","text":"x"}""");
        var pointer = string.Concat(Enumerable.Repeat("/a", 70));
        var update = Parse($$$"""[{"version":"1.0","updateDataModel":{"surfaceId":"s1","path":"{{{pointer}}}","value":1}}]""");

        var result = DysonA2uiSurfaces.Apply(first, null, update);

        Assert.True(result.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.Contains("deeper", result.Error.Message);
    }

    [Fact]
    public void update_without_surface_and_deleteSurface()
    {
        var update = Parse("""[{"version":"1.0","updateDataModel":{"surfaceId":"s1","value":{}}}]""");
        var missing = DysonA2uiSurfaces.Apply(null, null, update);
        Assert.True(missing.IsError);
        Assert.Contains("createSurface first", missing.Error.Message);

        var qid = Guid.NewGuid();
        var bound = Created("""{"id":"root","component":"Text","text":"x"}""", qid);
        Assert.Equal(qid, bound.QuestionId);
        var deleted = DysonA2uiSurfaces.Apply(bound, null, Parse("""[{"version":"1.0","deleteSurface":{"surfaceId":"s1"}}]"""));
        Assert.True(deleted.IsSuccess);
        Assert.Null(deleted.Value);
    }

    [Fact]
    public void ValidateTree_rejects_missing_root_dangling_child_and_cycle()
    {
        string[] bad =
        [
            """{"id":"main","component":"Text","text":"x"}""",
            """{"id":"root","component":"Column","children":["ghost"]}""",
            """{"id":"root","component":"Card","child":"a"},{"id":"a","component":"Card","child":"root"}""",
            """{"id":"root","component":"List","children":{"componentId":"ghost","path":"/items"}}""",
            """{"id":"root","component":"Card","child":"t"},{"id":"t","component":"Text","text":"x","weight":1}""",
            """{"id":"root","component":"Button","child":"t","action":{"event":{"name":"go"}}},{"id":"t","component":"Text","text":"x"}""",
            string.Join(",", Enumerable.Range(0, 17).Select(i => $$"""{"id":"{{(i == 0 ? "root" : "c" + i)}}","component":"Card","child":"c{{i + 1}}"}"""))
                + """,{"id":"c17","component":"Text","text":"x"}""",
        ];

        foreach (var components in bad)
        {
            var result = DysonA2uiSurfaces.Apply(null, null, Parse(DysonA2uiValidatorTests.Create(components, surfaceId: "s1")));
            Assert.True(result.IsError, components);
            Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
            Assert.StartsWith("/messages/0/createSurface", result.Error.Path);
        }
    }

    [Fact]
    public void ActionButtons_capped_at_MaxConversationActions_and_listed_in_order()
    {
        string Buttons(int n) =>
            $$"""{"id":"root","component":"Row","children":[{{string.Join(",", Enumerable.Range(0, n).Select(i => $"\"b{i}\""))}}]},"""
            + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"id":"b{{i}}","component":"ActionButton","label":"P{{i}}","func":"open_plan:{{i + 1}}"}"""));
        var catalog = $$""","catalogId":"{{DysonA2uiProtocol.DysonCatalogId}}" """;

        var eight = Created(Buttons(DysonWorkspaceToolExecutor.MaxConversationActions), extra: catalog);
        var funcs = DysonA2uiSurfaces.ActionButtonFuncs(eight);
        Assert.Equal(8, funcs.Count);
        Assert.Equal(("b0", "open_plan:1"), funcs[0]);

        var nine = DysonA2uiSurfaces.Apply(null, null, Parse(DysonA2uiValidatorTests.Create(Buttons(9), catalog, "s1")));
        Assert.True(nine.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, nine.Error.Code);
    }

    [Fact]
    public void OpenFolder_ActionButtons_count_toward_the_cap()
    {
        string Buttons(int n) =>
            $$"""{"id":"root","component":"Row","children":[{{string.Join(",", Enumerable.Range(0, n).Select(i => $"\"b{i}\""))}}]},"""
            + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"id":"b{{i}}","component":"ActionButton","label":"P{{i}}","func":"open_folder:dir{{i}}"}"""));
        var catalog = $$""","catalogId":"{{DysonA2uiProtocol.DysonCatalogId}}" """;

        var funcs = DysonA2uiSurfaces.ActionButtonFuncs(Created(Buttons(DysonWorkspaceToolExecutor.MaxConversationActions), extra: catalog));
        Assert.Equal(8, funcs.Count);
        Assert.Equal(("b7", "open_folder:dir7"), funcs[7]);

        var nine = DysonA2uiSurfaces.Apply(null, null, Parse(DysonA2uiValidatorTests.Create(Buttons(9), catalog, "s1")));
        Assert.True(nine.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, nine.Error.Code);
    }

    [Fact]
    public void QuestionSlotIds_in_component_order()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var surface = Created($$"""
            {"id":"root","component":"Column","children":["qa","qb"]},
            {"id":"qa","component":"QuestionSlot","@catalogId":"{{DysonA2uiProtocol.DysonCatalogId}}","questionId":"{{a}}"},
            {"id":"qb","component":"QuestionSlot","@catalogId":"{{DysonA2uiProtocol.DysonCatalogId}}","questionId":"{{b}}"}
            """);

        Assert.Equal([a, b], DysonA2uiSurfaces.QuestionSlotIds(surface));
    }

    [Fact]
    public void Persisted_surface_round_trips_and_invalid_row_deserializes_to_null()
    {
        var qid = Guid.NewGuid();
        var surface = Created("""
            {"id":"root","component":"Column","children":["t","a"]},
            {"id":"t","component":"Text","text":{"@path":"/m"}},
            {"id":"a","component":"ActionButton","label":"Plan","func":"open_plan:3"}
            """, qid, $$""","catalogId":"{{DysonA2uiProtocol.DysonCatalogId}}","dataModel":{"m":"hi"}""") with { LastError = "old error" };

        var json = DysonA2uiSurfaceSerializer.Serialize(surface);
        var back = DysonA2uiSurfaceSerializer.Deserialize(json);

        Assert.NotNull(back);
        Assert.Equal(surface.SurfaceId, back!.SurfaceId);
        Assert.Equal(qid, back.QuestionId);
        Assert.Equal(surface.Revision, back.Revision);
        Assert.Equal("old error", back.LastError);
        Assert.Equal(["root", "t", "a"], back.Components.Keys);
        Assert.Equal("hi", (string?)back.DataModel["m"]);
        Assert.Equal(json, DysonA2uiSurfaceSerializer.Serialize(back));

        var stub = new DysonA2uiSurface("s2", null, new Dictionary<string, JsonObject>(), [], 0, "boom");
        Assert.Equal("boom", DysonA2uiSurfaceSerializer.Deserialize(DysonA2uiSurfaceSerializer.Serialize(stub))!.LastError);

        Assert.Null(DysonA2uiSurfaceSerializer.Serialize(null));
        Assert.Null(DysonA2uiSurfaceSerializer.Deserialize(null));
        Assert.Null(DysonA2uiSurfaceSerializer.Deserialize("{"));
        Assert.Null(DysonA2uiSurfaceSerializer.Deserialize("""{"surfaceId":"s1","components":[{"id":"root","component":"Text","text":"x","html":"<b>"}],"dataModel":{},"revision":1}"""));
        Assert.Null(DysonA2uiSurfaceSerializer.Deserialize("""{"surfaceId":"s1","components":[{"id":"root","component":"ActionButton","@catalogId":"a2ui://dyson/catalogs/v1/catalog.json","label":"x","func":"deploy"}],"dataModel":{},"revision":1}"""));
        Assert.Null(DysonA2uiSurfaceSerializer.Deserialize("""{"surfaceId":"s1","components":[{"id":"main","component":"Text","text":"x"}],"dataModel":{},"revision":1}"""));
    }

    [Fact]
    public void Binding_write_resolve_expand_and_checks()
    {
        var model = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["n"] = "a" }, new JsonObject { ["n"] = "b" }) };

        var written = DysonA2uiBinding.Write(model, "/form/name", "Ann");
        Assert.True(written.IsSuccess);
        Assert.Null(model["form"]);
        Assert.Equal("Ann", DysonA2uiBinding.ResolveString(JsonNode.Parse("""{"@path":"/form/name"}"""), written.Value, ""));
        Assert.Equal("", DysonA2uiBinding.ResolveString(JsonNode.Parse("""{"@path":"/missing"}"""), written.Value, ""));
        Assert.False(DysonA2uiBinding.ResolveBool(JsonNode.Parse("""{"@path":"/missing"}"""), written.Value, ""));
        Assert.Null(DysonA2uiBinding.Write(written.Value, "/form/name", null).Value["form"]!["name"]);
        Assert.True(DysonA2uiBinding.Write(model, "bad", 1).IsError);

        var expanded = DysonA2uiBinding.ExpandChildren(JsonNode.Parse("""{"componentId":"row","path":"/items"}""")!, model);
        Assert.Equal([("row", "/items/0"), ("row", "/items/1")], expanded);
        Assert.Equal("Item b", DysonA2uiBinding.ResolveString(
            JsonNode.Parse("""{"@call":"formatString","args":{"value":"Item ${n}"}}"""), model, "/items/1"));

        var field = JsonNode.Parse("""
            {"id":"f","component":"TextField","label":"E","value":{"@path":"/e"},"@checks":[
              {"condition":{"@call":"required","args":{"value":{"@path":"/e"}}},"message":"Required"},
              {"condition":{"@call":"email","args":{"value":{"@path":"/e"}}},"message":"Email"},
              {"condition":{"@call":"length","args":{"value":{"@path":"/e"},"max":5}},"message":"Short"},
              {"condition":{"@call":"not","args":{"value":{"@path":"/off"}}},"message":"Not"}]}
            """)!.AsObject();
        Assert.Equal(["Required", "Email"], DysonA2uiBinding.FailedChecks(field, new JsonObject(), ""));
        Assert.Equal(["Short"], DysonA2uiBinding.FailedChecks(field, new JsonObject { ["e"] = "a@b.io!" }, ""));
        Assert.Empty(DysonA2uiBinding.FailedChecks(field, new JsonObject { ["e"] = "a@b.i" }, ""));
    }
}

using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>A2UI v1.0 shape/catalog validation: invalid input is a Result error, never a throw.</summary>
public class DysonA2uiValidatorTests
{
    internal const string Dyson = DysonA2uiProtocol.DysonCatalogId;

    internal static string Create(string components, string extra = "", string surfaceId = "flight_search") =>
        $$$"""[{"version":"1.0","createSurface":{"surfaceId":"{{{surfaceId}}}"{{{extra}}},"components":[{{{components}}}]}}]""";

    [Fact]
    public void Parse_valid_createSurface_returns_messages()
    {
        var json = $$$$"""
            [{"version":"1.0","createSurface":{"surfaceId":"flight_search","catalogId":"{{{{DysonA2uiProtocol.BasicCatalogId}}}}",
              "dataModel":{"flights":[{"from":"SFO"},{"from":"JFK"}],"form":{"note":""}},
              "components":[
                {"id":"root","component":"Card","child":"col"},
                {"id":"col","component":"Column","children":["title","list","note","agree","pick"],"align":"stretch"},
                {"id":"title","component":"Text","text":"Flights","variant":"caption"},
                {"id":"list","component":"List","children":{"componentId":"row","path":"/flights"}},
                {"id":"row","component":"Text","text":{"@call":"formatString","args":{"value":"From ${from}"}}},
                {"id":"note","component":"TextField","label":"Note","value":{"@path":"/form/note"},"weight":1,
                 "@checks":[{"condition":{"@call":"length","args":{"value":{"@path":"/form/note"},"max":20}},"message":"Too long"}]},
                {"id":"agree","component":"CheckBox","label":"OK","value":{"@path":"/form/ok"},"@accessibility":{"label":"Agree","live":"polite"}},
                {"id":"pick","component":"ChoicePicker","options":[{"label":"A","value":"a"}],"value":{"@path":"/form/pick"},"displayStyle":"chips"}
              ]}}]
            """;

        var result = DysonA2uiValidator.Parse(json);

        Assert.True(result.IsSuccess, result.IsError ? result.Error.ToWireJson() : null);
        var message = Assert.Single(result.Value);
        Assert.Equal(DysonA2uiMessageKind.CreateSurface, message.Kind);
        Assert.Equal("flight_search", message.SurfaceId);
        Assert.Equal("/messages/0/createSurface", message.Path);
        Assert.True(DysonA2uiSurfaces.Apply(null, null, result.Value).IsSuccess);
    }

    public static TheoryData<string, string> InvalidCases()
    {
        var text = """{"id":"root","component":"Text","text":"hi"}""";
        return new TheoryData<string, string>
        {
            { """{"version":"1.0"}""", "/messages" },
            { """[{"version":"0.9","createSurface":{"surfaceId":"s"}}]""", "/messages/0/version" },
            { """[{"version":"1.0","createSurface":{"surfaceId":"s"},"deleteSurface":{"surfaceId":"s"}}]""", "/messages/0" },
            { """[{"version":"1.0","callRendererFunction":{"surfaceId":"s"}}]""", "/messages/0/callRendererFunction" },
            { Create("""{"id":"root","component":"Svg","url":"https://x"}"""), "/messages/0/createSurface/components/0/component" },
            { Create("""{"id":"root","component":"Text","text":"x","html":"<b>"}"""), "/messages/0/createSurface/components/0/html" },
            {
                Create("""{"id":"root","component":"Button","child":"t","action":{"functionCall":{"@call":"openUrl","args":{"url":"https://x"}}}}"""),
                "/messages/0/createSurface/components/0/action/functionCall"
            },
            {
                Create("""{"id":"root","component":"Text","text":{"@call":"openUrl","args":{"url":"https://x"}}}"""),
                "/messages/0/createSurface/components/0/text/@call"
            },
            { Create(text, ""","catalogId":"https://evil.example/catalog.json" """), "/messages/0/createSurface/catalogId" },
            { Create(text, surfaceId: "bad-id"), "/messages/0/createSurface/surfaceId" },
            { Create("""{"id":"root","component":"Text","text":"x","@metadata":{}}"""), "/messages/0/createSurface/components/0/@metadata" },
            { Create(text, ""","sendDataModel":true"""), "/messages/0/createSurface/sendDataModel" },
            { Create(text, ""","theme":{}"""), "/messages/0/createSurface/theme" },
            { Create("""{"id":"root","component":"ChoicePicker","options":[{"label":"a","value":"a"}],"value":[],"filterable":true}"""), "/messages/0/createSurface/components/0/filterable" },
            { Create("""{"id":"root","component":"Text","text":"x","variant":"h6"}"""), "/messages/0/createSurface/components/0/variant" },
            { Create($$"""{"id":"root","component":"Text","text":"{{new string('x', DysonA2uiProtocol.MaxStringLength + 1)}}"}"""), "/messages/0/createSurface/components/0/text" },
            {
                """[{"version":"1.0","createSurface":{"surfaceId":"a"}},{"version":"1.0","deleteSurface":{"surfaceId":"b"}}]""",
                "/messages/1/deleteSurface/surfaceId"
            },
            { """[{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"no/slash","value":1}}]""", "/messages/0/updateDataModel/path" },
            { """[{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"/a"}}]""", "/messages/0/updateDataModel/value" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Parse_invalid_returns_VALIDATION_FAILED_with_pointer_without_throwing(string json, string path)
    {
        Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError>? result = null;
        Assert.Null(Record.Exception(() => result = DysonA2uiValidator.Parse(json)));
        Assert.True(result!.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.Equal(path, result.Error.Path);
    }

    [Fact]
    public void Parse_rejects_over_limit_inputs()
    {
        var many = "[" + string.Join(",", Enumerable.Repeat("""{"version":"1.0","deleteSurface":{"surfaceId":"s"}}""", 21)) + "]";
        Assert.Equal("/messages", DysonA2uiValidator.Parse(many).Error.Path);
        Assert.True(DysonA2uiValidator.Parse("[" + new string(' ', DysonA2uiProtocol.MaxArgumentBytes) + "]").IsError);
        Assert.True(DysonA2uiValidator.Parse("[{").IsError);
        Assert.True(DysonA2uiValidator.Parse("""[{"version":"1.0","version":"1.0","deleteSurface":{"surfaceId":"s"}}]""").IsError);
        Assert.Null(DysonA2uiSurfaceSerializer.Deserialize("""{"surfaceId":"s","surfaceId":"t"}"""));
        Assert.True(DysonA2uiValidator.Parse("[]").IsError);
    }

    [Fact]
    public void Error_wire_json_is_spec_shape()
    {
        var error = DysonA2uiValidator.Parse(Create("""{"id":"root","component":"Text","text":"x","html":"<b>"}""")).Error;
        var wire = JsonNode.Parse(error.ToWireJson())!;
        Assert.Equal("1.0", (string?)wire["version"]);
        Assert.Equal("VALIDATION_FAILED", (string?)wire["error"]!["code"]);
        Assert.Equal("flight_search", (string?)wire["error"]!["surfaceId"]);
        Assert.Equal("/messages/0/createSurface/components/0/html", (string?)wire["error"]!["path"]);
    }

    [Fact]
    public void Url_and_script_bearing_inputs_are_rejected()
    {
        var qid = Guid.NewGuid();
        string[] cases =
        [
            Create("""{"id":"root","component":"Image","url":"data:image/png;base64,AAAA"}"""),
            Create("""{"id":"root","component":"Icon","svgPath":"M0 0"}"""),
            Create("""{"id":"root","component":"Text","text":"x","onclick":"alert(1)"}"""),
            Create("""{"id":"root","component":"Text","text":"x","style":"color:red"}"""),
            Create("""{"id":"root","component":"Text","text":"x","@catalogId":"https://evil.example/catalog.json"}"""),
            Create("""{"id":"root","component":"Text","text":{"@call":"openUrl","args":{"url":"https://x"}}}"""),
            Create($$"""{"id":"root","component":"QuestionSlot","@catalogId":"{{Dyson}}","questionId":"{{qid}}","html":"<b>"}"""),
            Create($$"""{"id":"root","component":"QuestionSlot","@catalogId":"{{Dyson}}","questionId":"{{qid}}","url":"https://x"}"""),
            Create($$"""{"id":"root","component":"QuestionSlot","@catalogId":"{{Dyson}}","questionId":"{{qid}}","style":"x"}"""),
        ];

        foreach (var json in cases)
        {
            var result = DysonA2uiValidator.Parse(json);
            Assert.True(result.IsError, json);
            Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        }

        Assert.Empty(DysonA2uiCatalog.AllowedUrlPrefixes);
    }

    [Fact]
    public void QuestionSlot_shape_rules()
    {
        var qid = Guid.NewGuid();
        var ok = DysonA2uiValidator.Parse(Create($$"""{"id":"root","component":"QuestionSlot","@catalogId":"{{Dyson}}","questionId":"{{qid}}"}"""));
        Assert.True(ok.IsSuccess);
        Assert.True(DysonA2uiValidator.Parse(Create($$"""{"id":"root","component":"QuestionSlot","questionId":"{{qid}}"}""", $$""","catalogId":"{{Dyson}}" """)).IsSuccess);

        Assert.EndsWith("/component", DysonA2uiValidator.Parse(Create($$"""{"id":"root","component":"QuestionSlot","questionId":"{{qid}}"}""")).Error.Path);
        foreach (var bad in new[] { """{"@path":"/q"}""", "\"not-a-guid\"", $"\"{Guid.Empty}\"" })
        {
            var result = DysonA2uiValidator.Parse(Create($$"""{"id":"root","component":"QuestionSlot","@catalogId":"{{Dyson}}","questionId":{{bad}}}"""));
            Assert.True(result.IsError, bad);
            Assert.EndsWith("/components/0/questionId", result.Error.Path);
        }

        var reserved = DysonA2uiValidator.Parse(Create("""{"id":"root","component":"Text","text":"x"}""", surfaceId: "dyson_mine"));
        Assert.True(reserved.IsError);
        Assert.Contains("reserved", reserved.Error.Message);
        Assert.Null(reserved.Error.SurfaceId);
    }

    [Fact]
    public void ActionButton_unknown_func_is_Result_error()
    {
        Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError>? result = null;
        Assert.Null(Record.Exception(() => result = DysonA2uiValidator.Parse(Create(
            $$"""{"id":"root","component":"ActionButton","@catalogId":"{{Dyson}}","label":"Go","func":"deploy_now"}"""))));
        Assert.True(result!.IsError);
        Assert.Equal(DysonA2uiProtocol.ValidationFailed, result.Error.Code);
        Assert.EndsWith("/components/0/func", result.Error.Path);

        var basic = DysonA2uiValidator.Parse(Create("""{"id":"root","component":"ActionButton","label":"Go","func":"open_plan:1"}"""));
        Assert.True(basic.IsError);
        Assert.EndsWith("/components/0/component", basic.Error.Path);

        var dataBound = DysonA2uiValidator.Parse(Create(
            $$"""{"id":"root","component":"ActionButton","@catalogId":"{{Dyson}}","label":{"@path":"/l"},"func":"open_plan:1"}"""));
        Assert.EndsWith("/components/0/label", dataBound.Error.Path);
    }

    [Fact]
    public void ActionButtonFuncPointers_lists_funcs_with_pointer()
    {
        var parsed = DysonA2uiValidator.Parse(Create(
            $$"""
            {"id":"root","component":"Row","children":["a","b"]},
            {"id":"a","component":"ActionButton","@catalogId":"{{Dyson}}","label":"Plan","func":"open_plan:362"},
            {"id":"b","component":"ActionButton","@catalogId":"{{Dyson}}","label":"Site","func":"open_url:https://a2ui.org/"}
            """));

        Assert.True(parsed.IsSuccess);
        Assert.Equal(
            [("/messages/0/createSurface/components/1/func", "open_plan:362"), ("/messages/0/createSurface/components/2/func", "open_url:https://a2ui.org/")],
            DysonA2uiValidator.ActionButtonFuncPointers(parsed.Value));
    }
}

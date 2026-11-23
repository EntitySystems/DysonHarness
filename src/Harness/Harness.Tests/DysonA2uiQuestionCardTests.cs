using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>Derived question card, action resolve and the question bridge (no session).</summary>
public class DysonA2uiQuestionCardTests
{
    private static DysonUserQuestion Question(bool multi = false, bool custom = true, DysonUserQuestionAnswer? answer = null) =>
        new(Guid.NewGuid(), "Ship it?", ["A", "B", "C"], multi, custom, answer);

    [Fact]
    public void Build_derives_valid_surface_from_question()
    {
        var q = Question();

        var built = DysonA2uiQuestionCard.Build(q);

        Assert.True(built.IsSuccess, built.IsError ? built.Error.ToWireJson() : null);
        var s = built.Value;
        Assert.Equal("dyson_question_" + q.Id.ToString("N"), s.SurfaceId);
        Assert.Equal(q.Id, s.QuestionId);
        Assert.Equal("Card", (string?)s.Components["root"]["component"]);
        Assert.Equal("h5", (string?)s.Components["title"]["variant"]);
        Assert.Equal("ChoicePicker", (string?)s.Components["choices"]["component"]);
        Assert.Equal("mutuallyExclusive", (string?)s.Components["choices"]["variant"]);
        Assert.Equal(q.Choices, s.Components["choices"]["options"]!.AsArray().Select(o => (string)o!["value"]!));
        Assert.Equal("TextField", (string?)s.Components["custom"]["component"]);
        Assert.Equal("Button", (string?)s.Components["submit"]["component"]);
        Assert.Empty(s.DataModel["selected"]!.AsArray());

        Assert.False(DysonA2uiQuestionCard.Build(Question(custom: false)).Value.Components.ContainsKey("custom"));
        Assert.Equal("multipleSelection", (string?)DysonA2uiQuestionCard.Build(Question(multi: true)).Value.Components["choices"]["variant"]);
    }

    [Fact]
    public void Build_answered_question_has_no_submit_and_carries_answer()
    {
        var q = Question(custom: false, answer: new DysonUserQuestionAnswer(["B"], "note", DateTime.UtcNow));

        var s = DysonA2uiQuestionCard.Build(q).Value;

        Assert.False(s.Components.ContainsKey("submit"));
        Assert.True(s.Components.ContainsKey("custom"));
        Assert.Equal(["B"], s.DataModel["selected"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("note", (string?)s.DataModel["custom"]);
    }

    [Fact]
    public void Build_pre_existing_question_round_trips()
    {
        var q = Question();
        var loaded = DysonUserQuestionSerializer.Deserialize(DysonUserQuestionSerializer.Serialize(q))!;

        Assert.Equal(DysonA2uiSurfaceSerializer.Serialize(DysonA2uiQuestionCard.Build(q).Value),
            DysonA2uiSurfaceSerializer.Serialize(DysonA2uiQuestionCard.Build(loaded).Value));
        Assert.Equal(DysonA2uiQuestionCard.RenderKey(q), DysonA2uiQuestionCard.RenderKey(loaded));
        Assert.NotEqual(DysonA2uiQuestionCard.RenderKey(q), DysonA2uiQuestionCard.RenderKey(q with { Question = "Other?" }));
    }

    [Fact]
    public void Build_over_limit_question_is_Result_error()
    {
        var result = DysonA2uiQuestionCard.Build(Question() with { Question = new string('x', DysonA2uiProtocol.MaxStringLength + 1) });
        Assert.True(result.IsError);
    }

    [Fact]
    public void Derived_card_action_maps_to_answer()
    {
        var q = Question();
        var surface = DysonA2uiQuestionCard.Build(q).Value;
        var model = new JsonObject { ["selected"] = new JsonArray("B"), ["custom"] = "" };

        var action = DysonA2uiAction.Resolve(surface, "submit", model, DateTime.UtcNow);
        Assert.True(action.IsSuccess);
        Assert.Equal("answer", action.Value.Name);
        var wire = JsonNode.Parse(action.Value.ToWireJson())!;
        Assert.Equal(surface.SurfaceId, (string?)wire["action"]!["surfaceId"]);
        Assert.EndsWith("Z", (string?)wire["action"]!["timestamp"]);

        var answer = DysonA2uiQuestionBridge.ToAnswer(q, action.Value);
        Assert.True(answer.IsSuccess);
        Assert.Equal(["B"], answer.Value.Selected);
        Assert.Null(answer.Value.CustomText);
        Assert.True(DysonA2uiQuestionBridge.CanSubmit(q, surface, "submit", model).IsSuccess);

        var empty = DysonA2uiQuestionBridge.CanSubmit(q, surface, "submit", new JsonObject { ["selected"] = new JsonArray(), ["custom"] = "" });
        Assert.True(empty.IsError);
        Assert.True(DysonA2uiAction.Resolve(surface, "title", model, DateTime.UtcNow).IsError);
    }

    [Fact]
    public void ClearSelectionForCustom_clears_single_select_only_for_non_blank_text()
    {
        var single = Question();
        var surface = DysonA2uiQuestionCard.Build(single).Value;
        var model = new JsonObject { ["selected"] = new JsonArray("A"), ["custom"] = "use blender" };

        var cleared = DysonA2uiQuestionBridge.ClearSelectionForCustom(single, surface, model, "/custom", JsonValue.Create("use blender"));
        Assert.Empty(cleared["selected"]!.AsArray());
        Assert.True(DysonA2uiQuestionBridge.CanSubmit(single, surface, "submit", cleared).IsSuccess);

        Assert.Same(model, DysonA2uiQuestionBridge.ClearSelectionForCustom(single, surface, model, "/custom", JsonValue.Create("  ")));

        var multi = Question(multi: true);
        var multiSurface = DysonA2uiQuestionCard.Build(multi).Value;
        Assert.Same(model, DysonA2uiQuestionBridge.ClearSelectionForCustom(multi, multiSurface, model, "/custom", JsonValue.Create("x")));
    }

    [Fact]
    public void ToAnswer_maps_forwarded_form_and_rejects_bad_input()
    {
        var q = Question(multi: true);
        var surface = DysonA2uiSurfaces.Apply(null, q.Id, DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create("""
            {"id":"root","component":"Column","children":["pick","note","go"]},
            {"id":"pick","component":"ChoicePicker","options":[{"label":"A","value":"A"},{"label":"B","value":"B"}],"value":{"@path":"/sel"},"variant":"multipleSelection"},
            {"id":"note","component":"TextField","label":"Note","value":{"@path":"/note"}},
            {"id":"go","component":"Button","child":"go_label","action":{"event":{"name":"submit","context":{"selected":{"@path":"/sel"},"custom":{"@path":"/note"},"env":{"@path":"/env"}}}},
             "@checks":[{"condition":{"@call":"required","args":{"value":{"@path":"/sel"}}},"message":"Pick one"}]},
            {"id":"go_label","component":"Text","text":"Go"}
            """, surfaceId: "fwd")).Value).Value!;

        var model = new JsonObject { ["sel"] = new JsonArray("B"), ["note"] = "ship it", ["env"] = "prod" };
        var answer = DysonA2uiQuestionBridge.ToAnswer(q, DysonA2uiAction.Resolve(surface, "go", model, DateTime.UtcNow).Value);
        Assert.True(answer.IsSuccess);
        Assert.Equal(["B"], answer.Value.Selected);
        Assert.Equal("ship it\n{\"action\":\"submit\",\"context\":{\"env\":\"prod\"}}", answer.Value.CustomText);

        var unknown = DysonA2uiQuestionBridge.ToAnswer(q, DysonA2uiAction.Resolve(surface, "go", new JsonObject { ["sel"] = new JsonArray("Z") }, DateTime.UtcNow).Value);
        Assert.True(unknown.IsError);
        Assert.Contains("unknown choice", unknown.Error);

        var failingChecks = DysonA2uiAction.Resolve(surface, "go", new JsonObject(), DateTime.UtcNow);
        Assert.True(failingChecks.IsError);
        Assert.Equal("Pick one", failingChecks.Error.Message);
        Assert.Equal("Pick one", DysonA2uiQuestionBridge.CanSubmit(q, surface, "go", new JsonObject()).Error);

        var huge = new JsonObject { ["sel"] = new JsonArray("B"), ["note"] = "n", ["env"] = new string('e', DysonUserQuestion.MaxCustomAnswerLength) };
        var overLimit = DysonA2uiQuestionBridge.ToAnswer(q, DysonA2uiAction.Resolve(surface, "go", huge, DateTime.UtcNow).Value);
        Assert.True(overLimit.IsError);
        Assert.Contains("exceeds", overLimit.Error);

        var answered = q with { Answer = new DysonUserQuestionAnswer(["A"], null, DateTime.UtcNow) };
        Assert.Contains("already answered", DysonA2uiQuestionBridge.ToAnswer(answered, DysonA2uiAction.Resolve(surface, "go", model, DateTime.UtcNow).Value).Error);
    }
}

using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// The default meta question card as an A2UI surface derived from <see cref="DysonUserQuestion"/>.
/// Nothing is stored; it goes through the same validator and tree check as agent JSON.
/// </summary>
public static class DysonA2uiQuestionCard
{
    public static string SurfaceIdFor(Guid questionId) => DysonA2uiProtocol.QuestionSurfacePrefix + questionId.ToString("N");

    public static Result<DysonA2uiSurface, DysonA2uiError> Build(DysonUserQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answered = question.Answer is not null;
        var showCustom = question.AllowCustomAnswer || question.Answer?.CustomText is not null;

        var options = new JsonArray();
        foreach (var choice in question.Choices)
            options.Add(new JsonObject { ["label"] = choice, ["value"] = choice });

        var bodyChildren = new JsonArray("title", "choices");
        var components = new JsonArray
        {
            new JsonObject { ["id"] = "root", ["component"] = "Card", ["child"] = "body" },
            new JsonObject { ["id"] = "body", ["component"] = "Column", ["children"] = bodyChildren },
            new JsonObject { ["id"] = "title", ["component"] = "Text", ["text"] = question.Question, ["variant"] = "h5" },
            new JsonObject
            {
                ["id"] = "choices",
                ["component"] = "ChoicePicker",
                ["options"] = options,
                ["value"] = new JsonObject { ["@path"] = "/selected" },
                ["variant"] = question.MultiSelect ? "multipleSelection" : "mutuallyExclusive",
                ["displayStyle"] = "checkbox",
            },
        };

        if (showCustom)
        {
            bodyChildren.Add("custom");
            components.Add(new JsonObject
            {
                ["id"] = "custom",
                ["component"] = "TextField",
                ["label"] = "Other",
                ["value"] = new JsonObject { ["@path"] = "/custom" },
                ["variant"] = "longText",
            });
        }

        if (!answered)
        {
            bodyChildren.Add("submit");
            components.Add(new JsonObject
            {
                ["id"] = "submit",
                ["component"] = "Button",
                ["child"] = "submit_label",
                ["variant"] = "primary",
                ["action"] = new JsonObject
                {
                    ["event"] = new JsonObject
                    {
                        ["name"] = "answer",
                        ["context"] = new JsonObject
                        {
                            ["selected"] = new JsonObject { ["@path"] = "/selected" },
                            ["custom"] = new JsonObject { ["@path"] = "/custom" },
                        },
                    },
                },
            });
            components.Add(new JsonObject { ["id"] = "submit_label", ["component"] = "Text", ["text"] = "Submit answer" });
        }

        var selected = new JsonArray();
        foreach (var choice in question.Answer?.SelectedChoices ?? [])
            selected.Add(choice);

        var envelope = new JsonArray(new JsonObject
        {
            ["version"] = DysonA2uiProtocol.Version,
            ["createSurface"] = new JsonObject
            {
                ["surfaceId"] = SurfaceIdFor(question.Id),
                ["catalogId"] = DysonA2uiProtocol.BasicCatalogId,
                ["components"] = components,
                ["dataModel"] = new JsonObject { ["selected"] = selected, ["custom"] = question.Answer?.CustomText ?? "" },
            },
        });

        var parsed = DysonA2uiValidator.ParseCore(envelope, allowReservedSurfaceId: true);
        if (parsed.IsError)
            return Result<DysonA2uiSurface, DysonA2uiError>.AsError(parsed.Error);
        var applied = DysonA2uiSurfaces.Apply(null, question.Id, parsed.Value);
        if (applied.IsError)
            return Result<DysonA2uiSurface, DysonA2uiError>.AsError(applied.Error);
        return Result<DysonA2uiSurface, DysonA2uiError>.AsValue(applied.Value!);
    }

    /// <summary>Changes when the card is replaced or answered, so the view rebuilds with a fresh local model.</summary>
    public static string RenderKey(DysonUserQuestion question) =>
        DysonUserQuestionSerializer.Serialize(question) ?? "";
}

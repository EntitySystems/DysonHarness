using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>What a question-bound A2UI action answers: the same inputs as the plain card.</summary>
public sealed record DysonA2uiAnswer(IReadOnlyList<string> Selected, string? CustomText);

/// <summary>
/// Maps a resolved Button action to <c>(selected, customText)</c> for the existing answer path.
/// <c>context.selected</c> (string or string array) must match <see cref="DysonUserQuestion.Choices"/>;
/// <c>context.custom</c> (string) is the custom text; other context keys, when custom answers are allowed,
/// append <c>{"action":name,"context":{...}}</c> to the custom text.
/// </summary>
public static class DysonA2uiQuestionBridge
{
    public static Result<DysonA2uiAnswer, string> ToAnswer(DysonUserQuestion question, DysonA2uiActionEvent action)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(action);
        if (question.Answer is not null)
            return Result<DysonA2uiAnswer, string>.AsError("PostUserQuestion: already answered.");

        var selected = new List<string>();
        switch (action.Context["selected"])
        {
            case null:
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (DysonA2uiValidator.AsString(item) is not { } s)
                        return Result<DysonA2uiAnswer, string>.AsError("A2UI: context.selected must be a string or an array of strings.");
                    selected.Add(s);
                }

                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                if (v.GetValue<string>().Trim().Length > 0)
                    selected.Add(v.GetValue<string>());
                break;
            default:
                return Result<DysonA2uiAnswer, string>.AsError("A2UI: context.selected must be a string or an array of strings.");
        }

        string? custom = null;
        if (action.Context["custom"] is { } customNode)
        {
            custom = DysonA2uiValidator.AsString(customNode);
            if (custom is null)
                return Result<DysonA2uiAnswer, string>.AsError("A2UI: context.custom must be a string.");
            if (custom.Trim().Length == 0)
                custom = null;
        }

        if (question.AllowCustomAnswer)
        {
            var others = new JsonObject();
            foreach (var (key, value) in action.Context)
            {
                if (key is not ("selected" or "custom"))
                    others[key] = value?.DeepClone();
            }

            if (others.Count > 0)
            {
                if (custom is { Length: > DysonUserQuestion.MaxCustomAnswerLength })
                    return Result<DysonA2uiAnswer, string>.AsError("PostUserQuestion: custom answer cannot exceed 2000 characters.");
                var json = new JsonObject { ["action"] = action.Name, ["context"] = others }
                    .ToJsonString(DysonA2uiProtocol.WireOptions);
                custom = custom is null ? json : custom.Trim() + "\n" + json;
                if (custom.Length > DysonUserQuestion.MaxCustomAnswerLength)
                    custom = custom[..DysonUserQuestion.MaxCustomAnswerLength];
            }
        }

        var check = DysonUserQuestion.ValidateAnswer(question, selected, custom, DateTime.UtcNow);
        if (check.IsError)
            return Result<DysonA2uiAnswer, string>.AsError(check.Error);

        return Result<DysonA2uiAnswer, string>.AsValue(new DysonA2uiAnswer(check.Value.SelectedChoices, check.Value.CustomText));
    }

    /// <summary><see cref="DysonA2uiAction.Resolve"/> + <see cref="ToAnswer"/>, value discarded. Drives the Button's disabled state.</summary>
    public static VoidResult<string> CanSubmit(
        DysonUserQuestion question,
        DysonA2uiSurface surface,
        string sourceComponentId,
        JsonObject localModel)
    {
        var action = DysonA2uiAction.Resolve(surface, sourceComponentId, localModel, DateTime.UtcNow);
        if (action.IsError)
            return VoidResult<string>.AsError(action.Error.Message);
        var answer = ToAnswer(question, action.Value);
        return answer.IsError ? VoidResult<string>.AsError(answer.Error) : VoidResult<string>.Success;
    }
}

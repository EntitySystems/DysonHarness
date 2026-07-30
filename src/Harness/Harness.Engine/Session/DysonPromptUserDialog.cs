using System.Text.Json;

namespace DysonHarness;

/// <summary>One action button for PromptUserDialog / PromptUserDialogFromParent (max 4).</summary>
public sealed class DysonPromptUserDialogAction
{
    public required string Label { get; init; }
    public bool Primary { get; init; }
}

/// <summary>Parsed PromptUserDialog request shown in the host modal.</summary>
public sealed class DysonPromptUserDialogRequest
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<DysonPromptUserDialogAction> Actions { get; init; }
}

/// <summary>Parse + format helpers for PromptUserDialog tools (ponytail: shared schema, no framework).</summary>
public static class DysonPromptUserDialog
{
    public const int MaxActions = 4;
    public const string PromptUserDialogKind = "promptUserDialog";
    public const string SkipActionLabel = "Skip";

    public const string SkipGuidance =
        "User chose Skip. Continue your prescribed task; do not invent a new direction from this dialog.";

    public static Result<DysonPromptUserDialogRequest, string> ParseDialogJson(string? dialogJson)
    {
        if (string.IsNullOrWhiteSpace(dialogJson))
            return Result<DysonPromptUserDialogRequest, string>.AsError("dialog JSON is required.");

        try
        {
            using var doc = JsonDocument.Parse(dialogJson);
            return ParseDialogElement(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                "PromptUserDialog: invalid JSON — " + ex.Message);
        }
    }

    public static Result<DysonPromptUserDialogRequest, string> ParseDialogElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                "PromptUserDialog: expected a JSON object.");
        }

        if (!root.TryGetProperty("title", out var titleEl)
            || titleEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(titleEl.GetString()))
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                "PromptUserDialog: title is required.");
        }

        if (!root.TryGetProperty("description", out var descEl)
            || descEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(descEl.GetString()))
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                "PromptUserDialog: description is required.");
        }

        if (!root.TryGetProperty("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                "PromptUserDialog: actions array is required.");
        }

        var length = actionsEl.GetArrayLength();
        if (length == 0)
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                "PromptUserDialog: actions must be non-empty.");
        }

        if (length > MaxActions)
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                $"PromptUserDialog: at most {MaxActions} actions.");
        }

        var actions = new List<DysonPromptUserDialogAction>(length);
        var primaryCount = 0;
        var index = 0;
        foreach (var el in actionsEl.EnumerateArray())
        {
            index++;
            if (el.ValueKind != JsonValueKind.Object)
            {
                return Result<DysonPromptUserDialogRequest, string>.AsError(
                    $"PromptUserDialog: actions[{index}]: expected object.");
            }

            if (!el.TryGetProperty("label", out var labelEl)
                || labelEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(labelEl.GetString()))
            {
                return Result<DysonPromptUserDialogRequest, string>.AsError(
                    $"PromptUserDialog: actions[{index}]: label is required.");
            }

            var label = labelEl.GetString()!.Trim();
            if (string.Equals(label, SkipActionLabel, StringComparison.OrdinalIgnoreCase))
            {
                return Result<DysonPromptUserDialogRequest, string>.AsError(
                    $"PromptUserDialog: actions[{index}]: label '{SkipActionLabel}' is reserved for the UI Skip button.");
            }

            var primary = el.TryGetProperty("primary", out var primaryEl)
                && primaryEl.ValueKind is JsonValueKind.True;
            if (primary)
                primaryCount++;

            actions.Add(new DysonPromptUserDialogAction { Label = label, Primary = primary });
        }

        if (primaryCount > 1)
        {
            return Result<DysonPromptUserDialogRequest, string>.AsError(
                "PromptUserDialog: at most one action may be primary.");
        }

        return Result<DysonPromptUserDialogRequest, string>.AsValue(new DysonPromptUserDialogRequest
        {
            Title = titleEl.GetString()!.Trim(),
            Description = descEl.GetString()!.Trim(),
            Actions = actions,
        });
    }

    /// <summary>
    /// JSON tool result. Skip → <c>skipped: true</c> plus short guidance; otherwise chosen action label.
    /// </summary>
    public static string FormatResult(string actionLabel, bool skipped)
    {
        if (skipped)
        {
            return JsonSerializer.Serialize(new
            {
                action = SkipActionLabel,
                skipped = true,
                guidance = SkipGuidance,
            });
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(actionLabel);
        return JsonSerializer.Serialize(new
        {
            action = actionLabel.Trim(),
            skipped = false,
        });
    }

    /// <summary>Normalized dialog payload for parent-event / FromParent serialization.</summary>
    public static string SerializeRequest(DysonPromptUserDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(new
        {
            title = request.Title,
            description = request.Description,
            actions = request.Actions.Select(a => new { label = a.Label, primary = a.Primary }),
        });
    }

    public static string SharedDialogSchemaJson() =>
        """
        {
          "type": "object",
          "description": "Modal action picker: title, description, and 1–4 labeled actions (at most one primary). UI always adds a non-primary Skip.",
          "properties": {
            "title": { "type": "string", "description": "Dialog title shown to the user." },
            "description": { "type": "string", "description": "Short explanation of the decision." },
            "actions": {
              "type": "array",
              "minItems": 1,
              "maxItems": 4,
              "description": "Concrete action choices (not open-ended design questions).",
              "items": {
                "type": "object",
                "properties": {
                  "label": { "type": "string", "description": "Button label returned as the chosen action." },
                  "primary": {
                    "type": "boolean",
                    "description": "When true, render as the primary button. At most one action may be primary."
                  }
                },
                "required": ["label"]
              }
            }
          },
          "required": ["title", "description", "actions"]
        }
        """;
}

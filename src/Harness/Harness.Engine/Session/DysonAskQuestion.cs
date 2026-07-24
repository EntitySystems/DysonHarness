using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>Parsed AskQuestion / AskQuestionFromParent item (max 8 per call).</summary>
public sealed class DysonAskQuestionItem
{
    public required string Prompt { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public bool AllowMultiple { get; init; }
}

/// <summary>User answer for one question (options and/or custom text; Skip → empty Selected + Skipped).</summary>
public sealed class DysonAskQuestionAnswer
{
    public bool Skipped { get; init; }
    public IReadOnlyList<string> Selected { get; init; } = [];
    public string? Custom { get; init; }
}

/// <summary>Parse + format helpers for AskQuestion tools (ponytail: shared schema, no framework).</summary>
public static class DysonAskQuestion
{
    public const int MaxQuestions = 8;
    public const string AskQuestionKind = "askQuestion";

    public static Result<IReadOnlyList<DysonAskQuestionItem>, string> ParseQuestionsJson(string? questionsJson)
    {
        if (string.IsNullOrWhiteSpace(questionsJson))
            return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError("questions is required.");

        try
        {
            using var doc = JsonDocument.Parse(questionsJson);
            var root = doc.RootElement;

            JsonElement arrayEl;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arrayEl = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("questions", out var q)
                     && q.ValueKind == JsonValueKind.Array)
            {
                arrayEl = q;
            }
            else
            {
                return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                    "questions must be a JSON array or { \"questions\": [...] }.");
            }

            if (arrayEl.GetArrayLength() == 0)
                return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError("questions must be non-empty.");

            if (arrayEl.GetArrayLength() > MaxQuestions)
            {
                return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                    $"questions: at most {MaxQuestions} items.");
            }

            var items = new List<DysonAskQuestionItem>(arrayEl.GetArrayLength());
            var index = 0;
            foreach (var el in arrayEl.EnumerateArray())
            {
                index++;
                if (el.ValueKind != JsonValueKind.Object)
                {
                    return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                        $"questions[{index}]: expected object.");
                }

                if (!el.TryGetProperty("prompt", out var promptEl)
                    || promptEl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(promptEl.GetString()))
                {
                    return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                        $"questions[{index}]: prompt is required.");
                }

                if (!el.TryGetProperty("options", out var optionsEl) || optionsEl.ValueKind != JsonValueKind.Array)
                {
                    return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                        $"questions[{index}]: options array is required.");
                }

                var options = new List<string>();
                foreach (var opt in optionsEl.EnumerateArray())
                {
                    if (opt.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(opt.GetString()))
                    {
                        return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                            $"questions[{index}]: options must be non-empty strings.");
                    }

                    options.Add(opt.GetString()!.Trim());
                }

                if (options.Count == 0)
                {
                    return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                        $"questions[{index}]: options must be non-empty.");
                }

                var allowMultiple = el.TryGetProperty("allowMultiple", out var am)
                    && am.ValueKind is JsonValueKind.True;

                items.Add(new DysonAskQuestionItem
                {
                    Prompt = promptEl.GetString()!.Trim(),
                    Options = options,
                    AllowMultiple = allowMultiple,
                });
            }

            return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsValue(items);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<DysonAskQuestionItem>, string>.AsError(
                "questions: invalid JSON — " + ex.Message);
        }
    }

    /// <summary>
    /// Formats Q/A blocks. Skipped → <c>A# - [skipped]</c>. Custom text is appended after selected options.
    /// </summary>
    public static string FormatAnswers(
        IReadOnlyList<DysonAskQuestionItem> questions,
        IReadOnlyList<DysonAskQuestionAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);

        if (answers.Count != questions.Count)
            throw new ArgumentException("answers length must match questions.", nameof(answers));

        var sb = new StringBuilder();
        for (var i = 0; i < questions.Count; i++)
        {
            var n = i + 1;
            var q = questions[i];
            var a = answers[i];

            if (sb.Length > 0)
                sb.AppendLine();

            sb.Append("Q").Append(n).Append(" - ").AppendLine(q.Prompt);

            if (a.Skipped)
            {
                sb.Append("A").Append(n).Append(" - [skipped]");
                continue;
            }

            var parts = new List<string>();
            foreach (var sel in a.Selected)
            {
                if (!string.IsNullOrWhiteSpace(sel))
                    parts.Add(sel.Trim());
            }

            if (!string.IsNullOrWhiteSpace(a.Custom))
                parts.Add(a.Custom.Trim());

            var body = parts.Count == 0 ? "(no answer)" : string.Join(", ", parts);
            sb.Append("A").Append(n).Append(" - ").Append(body);
        }

        return sb.ToString();
    }

    public static string SharedQuestionsSchemaJson() =>
        """
        {
          "type": "array",
          "maxItems": 8,
          "description": "1–8 questions. Per-question Skip is allowed; allowMultiple permits multi-select; custom answers always allowed.",
          "items": {
            "type": "object",
            "properties": {
              "prompt": { "type": "string", "description": "Question text shown to the user." },
              "options": {
                "type": "array",
                "items": { "type": "string" },
                "minItems": 1,
                "description": "Suggested answer options (user may also type a custom answer)."
              },
              "allowMultiple": {
                "type": "boolean",
                "description": "When true, user may select multiple options. Default false."
              }
            },
            "required": ["prompt", "options"]
          }
        }
        """;
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// Meta DisplayInfo question card. The answer lives on the same record so one JSON
/// column locks the card. <see cref="DysonUserQuestionAnswer.AnsweredUtc"/> is UTC.
/// </summary>
public sealed record DysonUserQuestion(
    Guid Id,
    string Question,
    IReadOnlyList<string> Choices,
    bool MultiSelect,
    bool AllowCustomAnswer,
    DysonUserQuestionAnswer? Answer)
{
    public const int MaxCustomAnswerLength = 2000;

    /// <summary>
    /// One rule for the card button and <see cref="DysonAgentSession.TryRecordUserQuestionAnswer"/>.
    /// <paramref name="answeredUtc"/> is stamped by the session at record time.
    /// </summary>
    public static Result<DysonUserQuestionAnswer, string> ValidateAnswer(
        DysonUserQuestion question,
        IReadOnlyList<string>? selected,
        string? customText,
        DateTime answeredUtc)
    {
        ArgumentNullException.ThrowIfNull(question);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (selected is not null)
        {
            foreach (var raw in selected)
            {
                var trimmed = raw?.Trim() ?? "";
                if (trimmed.Length == 0 || !ContainsChoice(question.Choices, trimmed))
                    return Result<DysonUserQuestionAnswer, string>.AsError("PostUserQuestion: unknown choice.");
                if (!seen.Add(trimmed))
                    return Result<DysonUserQuestionAnswer, string>.AsError("PostUserQuestion: duplicate choice.");
            }
        }

        var ordered = new List<string>();
        foreach (var choice in question.Choices)
        {
            if (seen.Contains(choice))
                ordered.Add(choice);
        }

        var custom = string.IsNullOrWhiteSpace(customText) ? null : customText.Trim();
        if (custom is { Length: > MaxCustomAnswerLength })
            return Result<DysonUserQuestionAnswer, string>.AsError(
                "PostUserQuestion: custom answer cannot exceed 2000 characters.");
        if (!question.AllowCustomAnswer && custom is not null)
            return Result<DysonUserQuestionAnswer, string>.AsError(
                "PostUserQuestion: custom answer is not allowed.");

        if (!question.MultiSelect)
        {
            if (ordered.Count > 1)
                return Result<DysonUserQuestionAnswer, string>.AsError(
                    "PostUserQuestion: choose one option or a custom answer.");
            if (ordered.Count == 1 && custom is not null)
                return Result<DysonUserQuestionAnswer, string>.AsError(
                    "PostUserQuestion: choose one option or a custom answer, not both.");
            if (ordered.Count == 0 && custom is null)
                return Result<DysonUserQuestionAnswer, string>.AsError(
                    "PostUserQuestion: select at least one choice or a custom answer.");
        }
        else if (ordered.Count == 0 && custom is null)
        {
            return Result<DysonUserQuestionAnswer, string>.AsError(
                "PostUserQuestion: select at least one choice or a custom answer.");
        }

        return Result<DysonUserQuestionAnswer, string>.AsValue(
            new DysonUserQuestionAnswer(ordered, custom, AsUtc(answeredUtc)));
    }

    /// <summary>User bubble. Does not include the question id.</summary>
    public static string FormatVisibleInstruction(DysonUserQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = question.Answer;
        if (answer is null)
            return "Answer:";

        var choices = string.Join(", ", answer.SelectedChoices);
        if (choices.Length == 0)
            return $"Answer: {answer.CustomText}";
        if (string.IsNullOrEmpty(answer.CustomText))
            return $"Answer: {choices}";
        return $"Answer: {choices}; Other: {answer.CustomText}";
    }

    /// <summary>
    /// Model-only block. The DisplayInfo turn is omitted from provider transcripts,
    /// so the question text has to ride this hidden instruction.
    /// </summary>
    public static string FormatHiddenInstruction(DysonUserQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = question.Answer;
        var selected = answer is null ? "" : string.Join(" | ", answer.SelectedChoices);
        var custom = answer?.CustomText ?? "";
        var selectedLine = selected.Length == 0 ? "- selected:" : $"- selected: {selected}";
        var customLine = custom.Length == 0 ? "- custom:" : $"- custom: {custom}";
        return
            "User question answer:\n"
            + $"- questionId: {question.Id:D}\n"
            + $"- question: {question.Question}\n"
            + selectedLine + "\n"
            + customLine;
    }

    private static bool ContainsChoice(IReadOnlyList<string> choices, string trimmed)
    {
        foreach (var choice in choices)
        {
            if (string.Equals(choice, trimmed, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>Recorded card answer. <see cref="AnsweredUtc"/> is UTC, not <see cref="DateTimeOffset"/>.</summary>
public sealed record DysonUserQuestionAnswer(
    IReadOnlyList<string> SelectedChoices,
    string? CustomText,
    DateTime AnsweredUtc);

/// <summary>JSON for <c>turns.UserQuestionJson</c>. A bad row deserializes to null.</summary>
public static class DysonUserQuestionSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new UtcDateTimeConverter() },
    };

    public static string? Serialize(DysonUserQuestion? question)
    {
        if (question is null)
            return null;

        return JsonSerializer.Serialize(question, Options);
    }

    /// <summary>Null, whitespace, or malformed JSON is null so a bad row cannot fail session load.</summary>
    public static DysonUserQuestion? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        JsonQuestion? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonQuestion>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null
            || parsed.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(parsed.Question)
            || parsed.Choices is not { Count: > 0 })
        {
            return null;
        }

        var choices = new List<string>(parsed.Choices.Count);
        foreach (var choice in parsed.Choices)
        {
            var trimmed = choice?.Trim() ?? "";
            if (trimmed.Length == 0)
                return null;
            choices.Add(trimmed);
        }

        DysonUserQuestionAnswer? answer = null;
        if (parsed.Answer is { } raw)
        {
            var selected = new List<string>();
            if (raw.SelectedChoices is not null)
            {
                foreach (var item in raw.SelectedChoices)
                {
                    var trimmed = item?.Trim() ?? "";
                    if (trimmed.Length > 0)
                        selected.Add(trimmed);
                }
            }

            var custom = string.IsNullOrWhiteSpace(raw.CustomText) ? null : raw.CustomText.Trim();
            answer = new DysonUserQuestionAnswer(selected, custom, raw.AnsweredUtc);
        }

        return new DysonUserQuestion(
            parsed.Id,
            parsed.Question.Trim(),
            choices,
            parsed.MultiSelect,
            parsed.AllowCustomAnswer,
            answer);
    }

    private sealed class JsonQuestion
    {
        public Guid Id { get; set; }

        public string? Question { get; set; }

        public List<string>? Choices { get; set; }

        public bool MultiSelect { get; set; }

        public bool AllowCustomAnswer { get; set; }

        public JsonAnswer? Answer { get; set; }
    }

    private sealed class JsonAnswer
    {
        public List<string>? SelectedChoices { get; set; }

        public string? CustomText { get; set; }

        public DateTime AnsweredUtc { get; set; }
    }

    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !DateTime.TryParse(
                    reader.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return DateTime.SpecifyKind(default, DateTimeKind.Utc);
            }

            return parsed.Kind switch
            {
                DateTimeKind.Utc => parsed,
                DateTimeKind.Local => parsed.ToUniversalTime(),
                _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
            };
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
            writer.WriteStringValue(utc.ToString("o", CultureInfo.InvariantCulture));
        }
    }
}

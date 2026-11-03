using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>One masked field: stable key plus the label the user reads. Never a value.</summary>
public sealed record DysonSecureDataField(string Key, string Description);

/// <summary>
/// Submit / cancel outcome on the card. Key lists only. <see cref="CompletedUtc"/> is UTC.
/// </summary>
public sealed record DysonSecureDataOutcome(
    bool Cancelled,
    IReadOnlyList<string> Stored,
    IReadOnlyList<string> Skipped,
    DateTime CompletedUtc);

/// <summary>
/// Meta DisplayInfo RequestSecureData card (<c>turns.SecureDataRequestJson</c>). Holds keys,
/// descriptions, and the outcome. Secret values live only in <c>session_secrets</c>.
/// </summary>
public sealed record DysonSecureDataRequest(
    Guid Id,
    IReadOnlyList<DysonSecureDataField> Fields,
    DysonSecureDataOutcome? Outcome)
{
    public const int MaxFields = 8;
    public const string PendingTitle = "Enter secrets";

    /// <summary>User bubble for the answer turn. Keys only.</summary>
    public static string FormatAnswerPrompt(DysonSecureDataOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Cancelled || outcome.Stored.Count == 0 && outcome.Skipped.Count == 0)
            return "Secrets request cancelled. No keys stored.";
        if (outcome.Stored.Count == 0)
            return $"Secrets request submitted. No keys stored. Skipped: {string.Join(", ", outcome.Skipped)}.";
        return outcome.Skipped.Count == 0
            ? $"Secrets stored for keys: {string.Join(", ", outcome.Stored)}."
            : $"Secrets stored for keys: {string.Join(", ", outcome.Stored)}. Skipped: {string.Join(", ", outcome.Skipped)}.";
    }

    /// <summary>Model-only block. Keys and request id only; values are never passed in.</summary>
    public static string FormatHiddenInstruction(Guid requestId, DysonSecureDataOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var stored = outcome.Cancelled || outcome.Stored.Count == 0 ? "none" : string.Join(", ", outcome.Stored);
        var skipped = outcome.Skipped.Count == 0 ? "none" : string.Join(", ", outcome.Skipped);
        var head = outcome.Cancelled ? "Secure data request (cancelled by the user):\n" : "Secure data request:\n";
        return head
            + $"- requestId: {requestId:D}\n"
            + $"- stored: {stored}\n"
            + $"- skipped: {skipped}\n"
            + "Values were not included. Use WriteSecureDataToFile and pipe the file. Do not echo the secret.";
    }

    /// <summary>
    /// Splits a submission into stored/skipped keys in field order. A blank (empty or whitespace-only) or missing value is skipped.
    /// Unknown keys in <paramref name="values"/> are an error. Values are not copied into the result.
    /// </summary>
    public static Result<DysonSecureDataOutcome, string> BuildOutcome(
        DysonSecureDataRequest request,
        IReadOnlyDictionary<string, string>? values,
        bool cancelled,
        DateTime completedUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var utc = completedUtc.Kind == DateTimeKind.Utc ? completedUtc : completedUtc.ToUniversalTime();
        if (cancelled)
        {
            var all = request.Fields.Select(f => f.Key).ToList();
            return Result<DysonSecureDataOutcome, string>.AsValue(new DysonSecureDataOutcome(true, [], all, utc));
        }

        if (values is not null)
        {
            foreach (var (key, value) in values)
            {
                if (!request.Fields.Any(f => string.Equals(f.Key, key, StringComparison.Ordinal)))
                    return Result<DysonSecureDataOutcome, string>.AsError($"RequestSecureData: unknown key '{key}'.");
                if (value is { Length: > DysonSessionSecretLimits.MaxSecretLength })
                    return Result<DysonSecureDataOutcome, string>.AsError(
                        $"RequestSecureData: secret for '{key}' exceeds 8192 characters.");
            }
        }

        var stored = new List<string>();
        var skipped = new List<string>();
        foreach (var field in request.Fields)
        {
            if (values is not null && values.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                stored.Add(field.Key);
            else
                skipped.Add(field.Key);
        }

        return Result<DysonSecureDataOutcome, string>.AsValue(new DysonSecureDataOutcome(false, stored, skipped, utc));
    }

    /// <summary>Bubble body posted with the card: title plus one line per description.</summary>
    public static string FormatBubbleMessage(IReadOnlyList<DysonSecureDataField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return PendingTitle + "\n" + string.Join("\n", fields.Select(f => $"- {f.Description} ({f.Key})"));
    }

    /// <summary>One <c>pending_user_questions.ChoicesJson</c> entry: compact <c>{"key","description"}</c>.</summary>
    public static string EncodePendingChoice(DysonSecureDataField field) =>
        JsonSerializer.Serialize(new JsonField { Key = field.Key, Description = field.Description }, DysonSecureDataRequestSerializer.Options);

    /// <summary>Reverse of <see cref="EncodePendingChoice"/>. Null for a malformed entry.</summary>
    public static DysonSecureDataField? DecodePendingChoice(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonField>(choice, DysonSecureDataRequestSerializer.Options);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Key))
                return null;
            return new DysonSecureDataField(parsed.Key.Trim(), parsed.Description?.Trim() ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed class JsonField
    {
        public string? Key { get; set; }

        public string? Description { get; set; }
    }
}

/// <summary>JSON for <c>turns.SecureDataRequestJson</c>. A bad row deserializes to null.</summary>
public static class DysonSecureDataRequestSerializer
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? Serialize(DysonSecureDataRequest? request) =>
        request is null ? null : JsonSerializer.Serialize(request, Options);

    public static DysonSecureDataRequest? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        JsonRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonRequest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || parsed.Id == Guid.Empty || parsed.Fields is not { Count: > 0 })
            return null;

        var fields = new List<DysonSecureDataField>(parsed.Fields.Count);
        foreach (var field in parsed.Fields)
        {
            var key = field?.Key?.Trim() ?? "";
            if (key.Length == 0)
                return null;
            fields.Add(new DysonSecureDataField(key, field!.Description?.Trim() ?? ""));
        }

        DysonSecureDataOutcome? outcome = null;
        if (parsed.Outcome is { } raw)
        {
            var completed = raw.CompletedUtc.Kind switch
            {
                DateTimeKind.Utc => raw.CompletedUtc,
                DateTimeKind.Local => raw.CompletedUtc.ToUniversalTime(),
                _ => DateTime.SpecifyKind(raw.CompletedUtc, DateTimeKind.Utc),
            };
            outcome = new DysonSecureDataOutcome(
                raw.Cancelled,
                raw.Stored ?? [],
                raw.Skipped ?? [],
                completed);
        }

        return new DysonSecureDataRequest(parsed.Id, fields, outcome);
    }

    private sealed class JsonRequest
    {
        public Guid Id { get; set; }

        public List<DysonSecureDataRequest.JsonField?>? Fields { get; set; }

        public JsonOutcome? Outcome { get; set; }
    }

    private sealed class JsonOutcome
    {
        public bool Cancelled { get; set; }

        public List<string>? Stored { get; set; }

        public List<string>? Skipped { get; set; }

        public DateTime CompletedUtc { get; set; }
    }
}

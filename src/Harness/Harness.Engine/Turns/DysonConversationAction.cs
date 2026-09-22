using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// One meta-chat button. <see cref="FuncKey"/> is a lookup key, not source.
/// JSON name is <c>func</c>.
/// </summary>
public sealed record DysonConversationAction(
    string Name,
    [property: JsonPropertyName("func")] string FuncKey);

/// <summary>JSON for <c>turns.ConversationActionsJson</c> (name + func key only).</summary>
public static class DysonConversationActionsSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? Serialize(IReadOnlyList<DysonConversationAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
            return null;

        return JsonSerializer.Serialize(actions, Options);
    }

    /// <summary>
    /// Null, empty, whitespace, or malformed JSON is an empty list so a bad row cannot fail load.
    /// Entries with a blank name or func are skipped. Extra fields are ignored.
    /// </summary>
    public static List<DysonConversationAction> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        List<JsonItem>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<JsonItem>>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (parsed is null || parsed.Count == 0)
            return [];

        var list = new List<DysonConversationAction>(parsed.Count);
        foreach (var item in parsed)
        {
            if (item is null)
                continue;

            var name = item.Name?.Trim() ?? "";
            var key = item.Func?.Trim() ?? "";
            if (name.Length == 0 || key.Length == 0)
                continue;

            list.Add(new DysonConversationAction(name, key));
        }

        return list;
    }

    private sealed class JsonItem
    {
        public string? Name { get; set; }

        [JsonPropertyName("func")]
        public string? Func { get; set; }
    }
}

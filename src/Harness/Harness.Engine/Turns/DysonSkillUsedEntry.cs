using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// Skill attached to a turn (slash attach or <c>LoadSkill</c>). Included in provider transcripts + UI chip/modal.
/// </summary>
public sealed class DysonSkillUsedEntry
{
    public required string SkillId { get; init; }
    public required string DisplayName { get; init; }
    public required string MarkdownContent { get; init; }
    public string? ResolvedPath { get; init; }
    public bool LoadIndexOnly { get; init; }
    /// <summary>UTC.</summary>
    public DateTime UsedUtc { get; init; }
}

/// <summary>JSON serialize/restore helpers for <see cref="DysonAgentTurn.SkillsUsed"/>.</summary>
public static class DysonSkillsUsedSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? Serialize(IReadOnlyList<DysonSkillUsedEntry> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (skills.Count == 0)
            return null;

        return JsonSerializer.Serialize(skills, Options);
    }

    public static List<DysonSkillUsedEntry> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<DysonSkillUsedEntry>>(json, Options) ?? [];
    }
}

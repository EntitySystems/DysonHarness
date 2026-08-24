using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// Kind of a turn context-file attachment. <see cref="Skill"/> is 0 so missing JSON
/// <c>kind</c> on old <c>SkillsUsedJson</c> rows deserializes as a skill.
/// </summary>
public enum DysonContextFileKind
{
    Skill = 0,
    File = 1,
}

/// <summary>
/// Context file attached to a turn (slash / <c>LoadSkill</c> skill, or StartSubagent
/// <c>contextFiles</c> workspace file). Included in provider transcripts + UI chip/modal.
/// </summary>
public sealed class DysonContextFileEntry
{
    /// <summary>Stable id. JSON name stays <c>skillId</c> for existing <c>SkillsUsedJson</c> blobs.</summary>
    [JsonPropertyName("skillId")]
    public required string Id { get; init; }

    public required string DisplayName { get; init; }
    public required string MarkdownContent { get; init; }
    public string? ResolvedPath { get; init; }
    public bool LoadIndexOnly { get; init; }
    /// <summary>Normalized originating plugin id, when the skill came from a plugin package.</summary>
    public string? PluginId { get; init; }
    /// <summary>Package-relative source path, when the skill came from a plugin package.</summary>
    public string? PluginPackageRelativePath { get; init; }
    /// <summary>UTC.</summary>
    public DateTime UsedUtc { get; init; }

    /// <summary>Defaults to <see cref="DysonContextFileKind.Skill"/> when JSON omits <c>kind</c>.</summary>
    public DysonContextFileKind Kind { get; init; }
}

/// <summary>JSON serialize/restore helpers for <see cref="DysonAgentTurn.ContextFiles"/>.</summary>
public static class DysonContextFilesSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? Serialize(IReadOnlyList<DysonContextFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            return null;

        return JsonSerializer.Serialize(files, Options);
    }

    public static List<DysonContextFileEntry> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<DysonContextFileEntry>>(json, Options) ?? [];
    }
}

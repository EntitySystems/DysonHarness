using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// Persisted agent-mode tool denylists (<see cref="DysonAppSettingKeys.AgentModeToolPolicy"/>).
/// Missing mode / empty lists ⇒ all tools enabled (current default behavior).
/// </summary>
public sealed class DysonToolPolicyDocument
{
    /// <summary>Per built-in (or custom) agent mode denylist, keyed by mode name.</summary>
    [JsonPropertyName("modes")]
    public Dictionary<string, DysonToolPolicyModeEntry> Modes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-model overlays keyed by model slug Guid string.
    /// ponytail: schema + resolver hook only — v1 does not merge these.
    /// </summary>
    [JsonPropertyName("models")]
    public Dictionary<string, DysonToolPolicyModelEntry> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Disabled tool names for one agent mode.</summary>
public sealed class DysonToolPolicyModeEntry
{
    [JsonPropertyName("disabledTools")]
    public List<string> DisabledTools { get; set; } = [];
}

/// <summary>Per-model mode denylist overlay (unused in v1 resolve).</summary>
public sealed class DysonToolPolicyModelEntry
{
    [JsonPropertyName("modes")]
    public Dictionary<string, DysonToolPolicyModeEntry> Modes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

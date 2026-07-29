namespace DysonHarness;

/// <summary>Key/value preference persisted per subject.</summary>
public sealed class DysonAppSettingEntity
{
    /// <summary>Owning subject (composite PK with <see cref="Key"/>).</summary>
    public string SubjectId { get; set; } = "";

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

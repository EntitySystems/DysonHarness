namespace DysonHarness;

/// <summary>Key/value app preference persisted in SQLite.</summary>
public sealed class DysonAppSettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

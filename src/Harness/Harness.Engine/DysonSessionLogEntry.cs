namespace DysonHarness;

public sealed class DysonSessionLogEntry
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? TurnId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>Discriminator; typically <see cref="DysonSessionLogKind"/> name.</summary>
    public string Kind { get; set; } = "";

    public string PayloadJson { get; set; } = "{}";

    public DysonSessionEntity? Session { get; set; }
}

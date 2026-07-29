namespace DysonHarness;

/// <summary>Row for table <c>subjects</c>.</summary>
public sealed class DysonSubjectEntity
{
    public string Id { get; set; } = "";

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Reserved for future user binding; unused for now.</summary>
    public string? UserId { get; set; }
}

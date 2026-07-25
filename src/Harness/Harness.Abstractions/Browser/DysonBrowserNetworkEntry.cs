namespace DysonHarness;

public sealed class DysonBrowserNetworkEntry
{
    public required string Url { get; init; }
    public required string Method { get; init; }
    public int? Status { get; init; }
    public string? MimeType { get; init; }
    public long? DurationMs { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

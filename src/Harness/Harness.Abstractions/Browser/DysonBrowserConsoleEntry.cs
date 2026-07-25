namespace DysonHarness;

public sealed class DysonBrowserConsoleEntry
{
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? Source { get; init; }
    public int? Line { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

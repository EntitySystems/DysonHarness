namespace DysonHarness;

/// <summary>Typed mask describing what changed so a host can filter before re-rendering.</summary>
[Flags]
public enum DysonHostChangeKind
{
    None = 0,
    Streaming = 1 << 0,
    Transcript = 1 << 1,
    Busy = 1 << 2,
    SessionGraph = 1 << 3,
    Catalogs = 1 << 4,
    Overlay = 1 << 5,
    Error = 1 << 6,
    All = Streaming | Transcript | Busy | SessionGraph | Catalogs | Overlay | Error,
}

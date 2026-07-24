namespace DysonHarness;

public sealed class DysonSessionRenamedEventArgs : EventArgs
{
    public required Guid PersistenceId { get; init; }
    public required string Title { get; init; }
}

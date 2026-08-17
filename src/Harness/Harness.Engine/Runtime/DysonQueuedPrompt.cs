namespace DysonHarness;

/// <summary>
/// In-memory queued prompt held by <see cref="DysonSessionRuntime"/>.
/// The <see cref="Turn"/> reference is preserved (not serialized); <see cref="FilePaths"/>
/// is an immutable snapshot. Lost on process restart.
/// </summary>
public sealed class DysonQueuedPrompt
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SessionId { get; init; }

    public required DysonAgentTurn Turn { get; init; }

    public IReadOnlyList<string> FilePaths { get; init; } = [];
}

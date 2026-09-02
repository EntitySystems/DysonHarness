namespace DysonHarness;

public sealed record DysonSubagentSpawnedEvent(
    Guid ParentPersistenceId,
    Guid ChildPersistenceId,
    int RuntimeId,
    string Title,
    string AgentMode) : IDysonMessageBusEvent;

public sealed record DysonSubagentStatusChangedEvent(
    Guid PersistenceId,
    Guid? ParentPersistenceId,
    int RuntimeId,
    DysonSessionStatus Status,
    bool IsRunning,
    string? Summary) : IDysonMessageBusEvent;

public sealed record DysonSubagentActivityChangedEvent(
    Guid PersistenceId,
    int RuntimeId,
    string Title,
    string? LatestTurnStepTitle,
    bool IsRunning) : IDysonMessageBusEvent;

public sealed record DysonSessionTurnAddedEvent(
    Guid PersistenceId,
    Guid TurnId,
    DysonAgentTurnKind Kind) : IDysonMessageBusEvent;

public sealed record DysonParentEventsChangedEvent(
    Guid PersistenceId,
    bool HasPendingAsk,
    bool HasPendingUserDialog) : IDysonMessageBusEvent;

public sealed record DysonHostStateChangedEvent(
    DysonHostChangeKind Kind,
    Guid? SessionId) : IDysonMessageBusEvent;

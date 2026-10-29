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

/// <summary>Child SelectedProvider changed (immediate or pending pick applied). Not deduped.</summary>
public sealed record DysonSubagentModelChangedEvent(Guid PersistenceId, int RuntimeId) : IDysonMessageBusEvent;

public sealed record DysonSessionTurnAddedEvent(
    Guid PersistenceId,
    Guid TurnId,
    DysonAgentTurnKind Kind) : IDysonMessageBusEvent;

/// <summary>Live outgoing-context count changed mid-turn (per request and per response). Limit 0 = Off.</summary>
public sealed record DysonSessionContextTokensChangedEvent(
    Guid PersistenceId,
    int Tokens,
    int Limit) : IDysonMessageBusEvent;

/// <summary>Live context passed the effective limit; published at most once per turn.</summary>
public sealed record DysonSessionContextLimitReachedEvent(
    Guid PersistenceId,
    Guid TurnId,
    int Tokens,
    int Limit) : IDysonMessageBusEvent;

public sealed record DysonParentEventsChangedEvent(
    Guid PersistenceId,
    bool HasPendingAsk,
    bool HasPendingUserDialog) : IDysonMessageBusEvent;

public sealed record DysonHostStateChangedEvent(
    DysonHostChangeKind Kind,
    Guid? SessionId) : IDysonMessageBusEvent;

public sealed record DysonGitRepoChangedEvent(Guid WorkDirectoryId, string RepoRoot)
    : IDysonMessageBusEvent;

public sealed record DysonPlansChangedEvent(Guid WorkDirectoryId, long PlanId)
    : IDysonMessageBusEvent;

/// <summary>A Meta scratch file was created, edited, deleted, or moved. Carries no body.</summary>
public sealed record DysonMetaScratchFilesChangedEvent(Guid WorkDirectoryId, long ScratchFileId)
    : IDysonMessageBusEvent;

/// <summary>Which end of a voice-sidecar pair a bus message is addressed to.</summary>
public enum DysonVoiceSidecarDirection
{
    ToSidecar,
    ToMeta,
}

/// <summary>
/// One text message between a root Meta session and its voice sidecar.
/// The handler only writes the event onto a per-target channel; delivery is ordered by the reader.
/// </summary>
public sealed record DysonVoiceSidecarMessageEvent(
    Guid FromSessionId,
    Guid ToSessionId,
    DysonVoiceSidecarDirection Direction,
    string Text) : IDysonMessageBusEvent;

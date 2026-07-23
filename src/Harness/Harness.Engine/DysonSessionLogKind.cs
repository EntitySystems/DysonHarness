namespace DysonHarness;

public enum DysonSessionLogKind
{
    SessionCreated = 0,
    SessionResumed = 1,
    SessionStatusChanged = 2,
    UserPrompt = 3,
    TurnStarted = 4,
    TurnCompleted = 5,
    AgentReply = 6,
    ToolCallQueued = 7,
    ToolCallWorking = 8,
    ToolCallCompleted = 9,
    ToolCallFailed = 10,
    Interrupt = 11,
    ContextOptimized = 12,
    LogLine = 13,
    CompletionFlow = 14,
}

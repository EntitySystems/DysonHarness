using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

public sealed record DysonSessionLogSessionCreated(Guid SessionId, string AgentMode, int RuntimeId);

public sealed record DysonSessionLogSessionResumed(Guid SessionId);

public sealed record DysonSessionLogSessionStatusChanged(DysonSessionStatus Status, string? Reason = null);

public sealed record DysonSessionLogUserPrompt(string Prompt, IReadOnlyList<string>? FilePaths = null);

public sealed record DysonSessionLogTurnStarted(Guid TurnId, DysonAgentTurnKind Kind, string? AgentTitle = null);

public sealed record DysonSessionLogTurnCompleted(Guid TurnId, DysonAgentTurnKind Kind, string? AgentTitle = null);

public sealed record DysonSessionLogAgentReply(Guid TurnId, string? Title, string Body);

public sealed record DysonSessionLogToolCall(
    Guid TurnId,
    string CallId,
    string ToolName,
    string Stage,
    string? ArgumentsJson = null,
    string? ResultContent = null,
    bool? IsError = null);

public sealed record DysonSessionLogInterrupt(
    string InterruptKind,
    int? SubagentId = null,
    string? Summary = null,
    Guid? PersistenceId = null);

public sealed record DysonSessionLogContextOptimized(int TurnsCompacted, int? TokenEstimate = null);

public sealed record DysonSessionLogLogLine(string Line);

public sealed record DysonSessionLogCompletionFlow(string Phase, string? Detail = null);

public sealed record DysonSessionLogSessionRenamed(string Title);

/// <summary>
/// Kind-column discriminator + JSON payload helper (no deep abstraction).
/// </summary>
public static class DysonSessionLogPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string KindName(DysonSessionLogKind kind) => kind.ToString();

    public static bool TryParseKind(string? kind, out DysonSessionLogKind parsed) =>
        Enum.TryParse(kind, ignoreCase: true, out parsed);

    public static string Serialize<T>(T payload) =>
        JsonSerializer.Serialize(payload, Options);

    public static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        return JsonSerializer.Deserialize<T>(json, Options);
    }

    public static DysonSessionLogEntry CreateEntry(
        Guid sessionId,
        DysonSessionLogKind kind,
        object payload,
        Guid? turnId = null,
        long sequence = 0,
        DateTime? timestampUtc = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            TurnId = turnId,
            Sequence = sequence,
            TimestampUtc = timestampUtc ?? DateTime.UtcNow,
            Kind = KindName(kind),
            PayloadJson = Serialize(payload),
        };
}

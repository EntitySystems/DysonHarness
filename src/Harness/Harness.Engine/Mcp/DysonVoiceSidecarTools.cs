namespace DysonHarness;

/// <summary>
/// Voice Mode Meta Sidecar catalog: exactly eight tools, voice on or off.
/// <see cref="MessageVoiceModeSidecar"/> is the Meta Agent's replacement for SpeakToUser and is not
/// in <see cref="AllowedToolNames"/>.
/// </summary>
public static class DysonVoiceSidecarTools
{
    /// <summary>Exact sidecar catalog. Nothing else is kept.</summary>
    public static readonly IReadOnlyList<string> AllowedToolNames =
    [
        "SpeakToUser",
        "MessageMetaSession",
        "ReadMetaAgentLog",
        "ReadMemory",
        "EditMemory",
        "ListWorkdirs",
        "OpenWorkdirsPanel",
        "SwitchWorkdir",
    ];

    private static readonly HashSet<string> AllowedToolNameSet = new(AllowedToolNames, StringComparer.Ordinal);

    /// <summary>
    /// Sidecar only. Schema and the 600-character cap are unchanged from the old Meta definition.
    /// </summary>
    public static readonly DysonMcpTool SpeakToUser = new()
    {
        Name = "SpeakToUser",
        Description =
            "Speak text aloud to the user. At most 600 characters. Use short, plain sentences. Returns once queued. If the user starts talking, playback stops and their words arrive as your next message.",
        InputSchemaJson = """
            {
              "type": "object",
              "properties": {
                "text": { "type": "string", "description": "What to say aloud. Short plain-English sentences. At most 600 characters." }
              },
              "required": ["text"]
            }
            """,
    };

    /// <summary>Sidecar only. Returns as soon as the message is queued.</summary>
    public static readonly DysonMcpTool MessageMetaSession = new()
    {
        Name = "MessageMetaSession",
        Description =
            "Send text to your Meta Agent session. Returns as soon as it is queued: it never waits for a reply. The reply arrives later as a new message. The session is not interrupted.",
        InputSchemaJson = """
            {
              "type": "object",
              "properties": {
                "text": { "type": "string", "description": "What to tell the Meta session. Short and specific." }
              },
              "required": ["text"]
            }
            """,
    };

    /// <summary>Meta Agent only, and only while voice mode is on. Not in <see cref="AllowedToolNames"/>.</summary>
    public static readonly DysonMcpTool MessageVoiceModeSidecar = new()
    {
        Name = "MessageVoiceModeSidecar",
        Description =
            "Voice mode only. Send text to the voice sidecar, which speaks it to the user. Returns as soon as it is queued and never waits. Send the short version; put names, paths, and ids in PostConversationMessage instead.",
        InputSchemaJson = """
            {
              "type": "object",
              "properties": {
                "text": { "type": "string", "description": "The short version the sidecar should speak. No names, paths, or ids." }
              },
              "required": ["text"]
            }
            """,
    };

    /// <summary>Sidecar only. Read-only view of the owning Meta session.</summary>
    public static readonly DysonMcpTool ReadMetaAgentLog = new()
    {
        Name = "ReadMetaAgentLog",
        Description =
            "Read what your Meta Agent session is doing, without interrupting it. maxLines limits the log lines (default 80, max 400). Returns its status, whether it is busy, the turn in flight, recent log lines, and its last three chat posts, each cut to 1,000 characters. It does not return the session's full transcript.",
        InputSchemaJson = """
            {
              "type": "object",
              "properties": {
                "maxLines": { "type": "integer", "description": "How many recent log lines to return. Default 80, clamped to 1..400." }
              }
            }
            """,
    };

    /// <summary>Sidecar only.</summary>
    public static readonly DysonMcpTool ReadMemory = new()
    {
        Name = "ReadMemory",
        Description =
            "Read your private memory. Returns the text and its version. Empty until you first write it.",
        InputSchemaJson = """
            {
              "type": "object",
              "properties": {}
            }
            """,
    };

    /// <summary>Sidecar only. WriteFile shape minus path: content, old_text+new_text, or edits[].</summary>
    public static readonly DysonMcpTool EditMemory = new()
    {
        Name = "EditMemory",
        Description =
            "Change your private memory. Pass content to replace it all, or old_text and new_text for one change, or edits for several. old_text must match exactly once unless replace_all is true. A failed edit changes nothing. The text cannot grow past 16,384 characters.",
        InputSchemaJson = """
            {
              "type": "object",
              "properties": {
                "old_text": {
                  "type": "string",
                  "description": "Text span to replace (single edit). Must be unique unless replace_all. Copy from ReadMemory's content."
                },
                "new_text": { "type": "string", "description": "Replacement text for old_text." },
                "replace_all": {
                  "type": "boolean",
                  "description": "If true, replace every occurrence of old_text (default false). Also applies as default for edits[] items unless overridden."
                },
                "edits": {
                  "type": "array",
                  "description": "Ordered list of targeted replacements when multiple hunks are needed.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "old_text": { "type": "string", "description": "Text span to replace." },
                      "new_text": { "type": "string" },
                      "replace_all": { "type": "boolean", "description": "Replace every occurrence for this edit (default: top-level replace_all)." }
                    },
                    "required": ["old_text", "new_text"]
                  }
                },
                "content": {
                  "type": "string",
                  "description": "Replace the whole memory. Only when targeted edits are impractical; prefer old_text/edits."
                }
              }
            }
            """,
    };

    /// <summary>
    /// Strips the pipeline to exactly <see cref="AllowedToolNames"/> and restores those eight definitions.
    /// Authoritative for the sidecar mode.
    /// </summary>
    public static void ApplyAllowlist(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        foreach (var name in pipeline.Tools.Keys.ToArray())
        {
            if (!AllowedToolNameSet.Contains(name))
                pipeline.Tools.Remove(name);
        }

        pipeline.Tools[SpeakToUser.Name] = SpeakToUser;
        pipeline.Tools[MessageMetaSession.Name] = MessageMetaSession;
        pipeline.Tools[ReadMetaAgentLog.Name] = ReadMetaAgentLog;
        pipeline.Tools[ReadMemory.Name] = ReadMemory;
        pipeline.Tools[EditMemory.Name] = EditMemory;
        foreach (var tool in DysonMetaAgentTools.CreateWorkdirTools())
            pipeline.Tools[tool.Name] = tool;
    }
}

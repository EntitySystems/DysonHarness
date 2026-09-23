namespace DysonHarness;

/// <summary>
/// Meta Agent / Meta Agent Drone catalog: allowlist strip + meta-only tool definitions.
/// Browser tools are kept when already on the pipeline; project file, shell, search, wait, and completion tools stay absent.
/// <c>WriteTempFile</c> and <c>ReadTempFile</c> are the only file tools, and they only touch generated files under <c>.dyson/temp/</c>.
/// </summary>
public static class DysonMetaAgentTools
{
    public const string LoadSkillLiteralRejectedMessage =
        "LoadSkill in Meta Agent mode resolves named skills and openrules entries only, not file paths.";

    public const string PlanIdMustBePositiveMessage = "planId must be a positive integer";

    public const string SetPlanStatusAcceptedMessage =
        "SetPlanStatus: status must be 'building', 'completed', or 'stale'.";

    /// <summary>Shared tools kept from the default catalog (canonical schemas).</summary>
    private static readonly string[] SharedToolNames =
    [
        "SummarizeTurns",
        "CreateTodo",
        "ListTodos",
        "UpdateTodo",
        "GetOpenRulesConfig",
        "LoadSkill",
    ];

    /// <summary>Exact Meta Agent catalog when browser tools are not on the pipeline (no project file/shell/wait/completion tools). <c>WriteTempFile</c> and <c>ReadTempFile</c> are temp-scoped. Browser names are not listed here.</summary>
    public static readonly IReadOnlyList<string> AllowedToolNames =
    [
        "BeginBuildPlan",
        "CanCreateNote",
        "CompactConversation",
        "CreateAsyncMetaAgentDrone",
        "CreateNote",
        "CreateTodo",
        "DeleteMetaAgent",
        "DeleteNote",
        "DeletePlan",
        "GetOpenRulesConfig",
        "ListMetaAgentDrones",
        "ListNotes",
        "ListPlans",
        "ListTodos",
        "LoadSkill",
        "MessageMetaAgentDrone",
        "PostConversationMessage",
        "ReadMetaAgentDroneLog",
        "ReadTempFile",
        "RemoveTodos",
        "RenderHtmlVisualization",
        "RespondToSubagentEvent",
        "SetPlanStatus",
        "StartAsyncExploreAgent",
        "StopMetaAgentDrone",
        "SummarizeTurns",
        "UpdateNote",
        "UpdateTodo",
        "WriteTempFile",
    ];

    /// <summary>
    /// Names that must never appear in the Meta Agent catalog.
    /// The no-filesystem / never-block invariant of this mode.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedToolNames =
    [
        "ReadFile",
        "WriteFile",
        "Grep",
        "ListDirectory",
        "LoadBinary",
        "CreateFile",
        "CreateDirectory",
        "ShellExecute",
        "StartLongRunningShell",
        "InitializeOpenRules",
        "SubmitPlan",
        "EditPlan",
        "AskQuestion",
        "PromptUserDialog",
        "WaitForSubagent",
        "CompleteTask",
        "ConfirmTaskComplete",
        "ContinueWork",
        "SubmitSubagentReport",
        "DropTurnContext",
        "RestoreTurnContext",
        "StartNewTurn",
        "ExpandThoughtProcess",
        "ResumeCurrentTask",
        "GetDateTime",
        "RenameSession",
        "StartSubagent",
        "ListSubagents",
        "InspectSubagentLog",
        "StopSubagent",
        "TriggerSubagentEvent",
        "TriggerParentEvent",
        "AskQuestionFromParent",
        "PromptUserDialogFromParent",
        "DeleteTodo",
        "WaitForSeconds",
    ];

    private static readonly HashSet<string> AllowedToolNameSet = new(AllowedToolNames, StringComparer.Ordinal);

    /// <summary>
    /// Names from <see cref="DysonMcpPipeline.CreateBrowserTools"/>.
    /// Kept only when already on the pipeline (<c>BrowserControl</c> set and not denylisted).
    /// </summary>
    private static readonly HashSet<string> BrowserToolNameSet = new(
        DysonMcpPipeline.CreateBrowserTools().Select(static tool => tool.Name),
        StringComparer.Ordinal);

    /// <summary>
    /// Strips everything not in <see cref="AllowedToolNames"/>, except browser tools already on the pipeline.
    /// Restores shared schemas from a no-browser <c>CreateDefault</c>, then adds meta-only tools.
    /// Authoritative for Meta Agent mode.
    /// </summary>
    public static void ApplyAllowlist(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        foreach (var name in pipeline.Tools.Keys.ToArray())
        {
            if (!AllowedToolNameSet.Contains(name) && !BrowserToolNameSet.Contains(name))
                pipeline.Tools.Remove(name);
        }

        var source = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        foreach (var name in SharedToolNames)
        {
            if (source.Tools.TryGetValue(name, out var shared))
                pipeline.Tools[name] = shared;
        }

        foreach (var tool in CreateMetaAgentTools())
            pipeline.Tools[tool.Name] = tool;
    }

    /// <summary>
    /// Meta Agent Drone: keep the Work catalog (including <c>TriggerParentEvent</c>),
    /// drop Ask/dialog FromParent tools, add <c>ReadMetaPlan</c> and a <c>SubmitMetaPlan</c> seam.
    /// </summary>
    public static void ApplyDroneAllowlist(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        pipeline.Tools.Remove("AskQuestionFromParent");
        pipeline.Tools.Remove("PromptUserDialogFromParent");

        foreach (var tool in CreateMetaAgentDroneTools())
            pipeline.Tools[tool.Name] = tool;
    }

    private static IEnumerable<DysonMcpTool> CreateMetaAgentTools()
    {
        yield return new DysonMcpTool
        {
            Name = "CreateAsyncMetaAgentDrone",
            Description =
                "Spawn a Meta Agent Drone (non-blocking). " +
                "File-mutating tasks (writing code, editing the repo) should set useWorktree true; non-coding tasks (ops, testing, CI, pushes, read-and-run) should set useWorktree false. " +
                "true: own git worktree and branch, merged on completion. false: parent's work directory, no branch, no merge. " +
                "Optional existingWorktreePath (only with useWorktree false) rebinds to an already-listed worktree without allocating one. " +
                "Returns immediately with droneId / persistenceId / worktreeBranch (null when useWorktree is false); never waits. " +
                "purpose=build (default) implements; purpose=plan explores then SubmitMetaPlan. " +
                "Call ListMetaAgentDrones before dispatching. Reuse an existing drone with MessageMetaAgentDrone " +
                "instead of spawning a second one for the same work.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "task": { "type": "string", "description": "Assigned task brief for the drone. Goal, constraints, and acceptance criteria." },
                    "useWorktree": {
                      "type": "boolean",
                      "description": "Required, no default. File-mutating tasks (writing code, editing the repo) should set useWorktree true; non-coding tasks (ops, testing, CI, pushes, read-and-run) should set useWorktree false. true forks a git worktree and merges on completion; false stays on the parent checkout with no branch and no merge."
                    },
                    "existingWorktreePath": {
                      "type": "string",
                      "description": "Optional. Only with useWorktree false. Absolute path of an already-listed worktree. Rebinds tools there, does not call Ensure, and does not set worktree columns so completion does not merge. Omit to stay on the registered checkout. Error if set with useWorktree true."
                    },
                    "purpose": {
                      "type": "string",
                      "enum": ["build", "plan"],
                      "description": "build (default) implements; plan explores then SubmitMetaPlan. Do not implement a plan-authoring brief."
                    },
                    "context": { "type": "string", "description": "Optional extra context or constraints." },
                    "todos": {
                      "type": "array",
                      "description": "Optional seed checklist for the drone's own session todo list.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "displayName": { "type": "string", "description": "Human-readable todo title." },
                          "taskCode": { "type": "string", "description": "Stable code unique within the child session." },
                          "status": {
                            "type": "string",
                            "enum": ["pending", "ongoing", "complete"],
                            "description": "Initial status (default: pending)."
                          },
                          "comments": {
                            "type": "array",
                            "items": { "type": "string" },
                            "description": "Optional initial comments."
                          }
                        },
                        "required": ["displayName", "taskCode"]
                      }
                    },
                    "modelSlug": {
                      "type": "string",
                      "description": "Optional model slug or display alias. Omit to inherit the parent session model."
                    },
                    "reasoningEffort": {
                      "type": "string",
                      "description": "Optional freeform reasoning_effort. Omit keeps the parent or slug default."
                    }
                  },
                  "required": ["task", "useWorktree"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "StartAsyncExploreAgent",
            Description =
                "Spawn a read-only Explore agent (non-blocking). Returns immediately with agentId / persistenceId; never waits. " +
                "Use before briefing a drone when you need a fact about the code. " +
                "Call ListMetaAgentDrones before dispatching.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "task": { "type": "string", "description": "Assigned investigation brief." },
                    "context": { "type": "string", "description": "Optional extra context or constraints." },
                    "contextFiles": {
                      "type": "array",
                      "description": "Optional work-relative file paths preloaded onto the explore's first turn as File context.",
                      "items": { "type": "string", "description": "Work-relative or workspace file path." }
                    }
                  },
                  "required": ["task"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListMetaAgentDrones",
            Description =
                "Roster of this session's drones and explores, including last report. " +
                "Call before dispatching: old turns are deleted permanently, so an id you cannot see may still be a running drone. " +
                "Plain stable projection — no notices or counts.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ReadMetaAgentDroneLog",
            Description =
                "Read recent log lines for a drone or explore by agentId. " +
                "Do not idle-poll; read only when the user asks about progress or a report looks wrong.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "agentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Runtime id of the drone or explore."
                    },
                    "maxLines": {
                      "type": "integer",
                      "description": "Optional max number of recent log lines to return."
                    }
                  },
                  "required": ["agentId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "StopMetaAgentDrone",
            Description =
                "Stop a drone or explore. A stopped drone's worktree is left for inspection, not merged; " +
                "pass discardWorktree to throw that work away. Non-blocking.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "agentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Runtime id of the drone or explore."
                    },
                    "reason": { "type": "string", "description": "Optional reason recorded on the stopped session." },
                    "discardWorktree": {
                      "type": "boolean",
                      "description": "When true, remove the worktree checkout and clear worktree columns (default false)."
                    }
                  },
                  "required": ["agentId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "MessageMetaAgentDrone",
            Description =
                "Send a follow-up instruction to an existing drone or explore (reopens a terminal drone). " +
                "Prefer this over creating a second drone for the same work. Non-blocking.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "agentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Runtime id of the drone or explore."
                    },
                    "message": { "type": "string", "description": "Instruction injected into the child." },
                    "interrupt": {
                      "type": "boolean",
                      "description": "When true, cancel the in-flight child turn and run immediately (default false = queue)."
                    }
                  },
                  "required": ["agentId", "message"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "DeleteMetaAgent",
            Description =
                "Permanently delete a finished drone or explore and its children. " +
                "Refuses while the agent or any descendant is still running, and refuses while its worktree is unmerged. " +
                "Before deleting, keep anything worth keeping in a todo or a posted message.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "agentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Runtime id of a direct child of this Meta Agent session."
                    },
                    "reason": { "type": "string", "description": "Why this agent is being deleted (required)." }
                  },
                  "required": ["agentId", "reason"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "PostConversationMessage",
            Description =
                "Post markdown the user should see in the meta conversation. " +
                "Assistant text is not shown; use this for everything the user should see. " +
                "Optional actions add buttons { name, func } where func is a key registered on this session, not source code. " +
                "Buttons are not run until the user clicks. Omit actions or pass [] for a plain markdown bubble. " +
                "visualizationId is separate from actions. " +
                "Does not end the turn.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "message": { "type": "string", "description": "Markdown posted as a DisplayInfo turn." },
                    "actions": {
                      "type": "array",
                      "maxItems": 8,
                      "description": "Optional buttons. Not run until the user clicks. Each func is a key already registered on this session, not source code. Omit or [] for a plain markdown bubble.",
                      "items": {
                        "type": "object",
                        "required": ["name", "func"],
                        "properties": {
                          "name": { "type": "string", "description": "Button label." },
                          "func": { "type": "string", "description": "Lookup key. Not source code." }
                        }
                      }
                    },
                    "visualizationId": {
                      "type": "string",
                      "description": "Optional GUID returned by RenderHtmlVisualization. Adds one button that opens that visualization. Not a func key. Omit or null for no visualization button."
                    }
                  },
                  "required": ["message"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CompactConversation",
            Description =
                "Enqueue a full session summary that replaces earlier turns in later transcripts. " +
                "Use before the 40-turn cap bites, or whenever the thread drifts.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "RemoveTodos",
            Description =
                "Remove todos by taskCode that are no longer going to happen. Meta Agent only. " +
                "reason is required.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "taskCodes": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Task codes to remove."
                    },
                    "reason": { "type": "string", "description": "Why these todos are being removed." }
                  },
                  "required": ["taskCodes", "reason"]
                }
                """,
        };

        // Plan tools: addressed by planId only — no path argument.
        yield return new DysonMcpTool
        {
            Name = "ListPlans",
            Description =
                "List plans for this work directory (planId, title, status, buildAgentId, updatedUtc). " +
                "No path, no body, no note. Use after compaction to recover planIds.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SetPlanStatus",
            Description =
                "Update a plan's status by planId. Accepts building, completed, or stale. Draft cannot be set back.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "planId": { "type": "integer", "description": "Positive plan id." },
                    "status": {
                      "type": "string",
                      "enum": ["building", "completed", "stale"],
                      "description": "New status."
                    },
                    "note": { "type": "string", "description": "Optional status note." }
                  },
                  "required": ["planId", "status"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BeginBuildPlan",
            Description =
                "Dispatch a Meta Agent Drone briefed on planId (or message an existing drone via agentId) and set status to building. " +
                "The brief names the planId, not the plan body — the drone calls ReadMetaPlan.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "planId": { "type": "integer", "description": "Positive plan id." },
                    "extraInstructions": { "type": "string", "description": "Optional extra instructions appended to the build brief." },
                    "agentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Reuse this existing drone instead of spawning a new one."
                    }
                  },
                  "required": ["planId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "DeletePlan",
            Description = "Permanently delete a plan row by planId. Irreversible. No filesystem involvement.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "planId": { "type": "integer", "description": "Positive plan id." },
                    "reason": { "type": "string", "description": "Why this plan is being deleted." }
                  },
                  "required": ["planId", "reason"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListNotes",
            Description =
                "List your scratch notes. Returns each note's name and token count, never the note text. Call this to see what you already have before you update or delete. This does not say whether a write fits; call CanCreateNote before CreateNote or UpdateNote.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CanCreateNote",
            Description =
                "Check whether a scratch-note write is allowed. Call this before every CreateNote or UpdateNote. Caps: 20 notes, 1000 tokens each, 20000 tokens total. Optional name and content check that write (a new name is a create, an existing name is an update). With no arguments, reports whether another note can be created, plus the current totals. Returns allowed, reason, noteCount, totalTokens, and the caps. Does not list note names; call ListNotes for that. Does not write.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Optional note name. A new name is a create; an existing name is an update." },
                    "content": { "type": "string", "description": "Optional proposed full text. May be empty." }
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CreateNote",
            Description =
                "Create a new scratch note. Call CanCreateNote first. name is the note's name and must end in .md, for example guidelines.md. content is the full text and may be empty. The text must be at most 1000 tokens. Fails if that name already exists (use UpdateNote), if this would be the 21st note, or if all notes together would pass 20000 tokens. Returns the name and the token count.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Note name ending in .md, for example guidelines.md." },
                    "content": { "type": "string", "description": "Full text. May be empty." }
                  },
                  "required": ["name", "content"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "UpdateNote",
            Description =
                "Edit an existing scratch note. Call CanCreateNote first. path is the note name from ListNotes, the same kind of name CreateNote takes, for example guidelines.md. Pass content to replace the whole note, or old_text and new_text, or edits. The note after the edit must be at most 1000 tokens, and all notes together must stay at most 20000. A missing name is an error; use CreateNote.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Path of the file to update." },
                    "old_text": {
                      "type": "string",
                      "description": "Text span to replace (single edit). Must be unique unless replace_all. Do not include ReadFile 'N|' prefixes."
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
                          "old_text": { "type": "string", "description": "Text span to replace. No ReadFile 'N|' prefixes." },
                          "new_text": { "type": "string" },
                          "replace_all": { "type": "boolean", "description": "Replace every occurrence for this edit (default: top-level replace_all)." }
                        },
                        "required": ["old_text", "new_text"]
                      }
                    },
                    "content": {
                      "type": "string",
                      "description": "Full-file rewrite only when targeted edits are impractical."
                    }
                  },
                  "required": ["path"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "DeleteNote",
            Description =
                "Delete one scratch note by the name ListNotes showed. Allowed even when you are at 20 notes or a note is over 1000 tokens, so a bad note can be removed. A missing name is an error. No token check.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Note name ending in .md." }
                  },
                  "required": ["name"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WriteTempFile",
            Description =
                "Write a temporary visualization asset under .dyson/temp/. path is a leaf file name with an extension, such as chart.html, chart.css, or chart.js. The harness sanitizes it, inserts a random suffix before the extension, and returns the exact workspace-relative path. Pass that path verbatim as a RenderHtmlVisualization tempFile in a later stage. This does not create or overwrite project files.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "path": {
                      "type": "string",
                      "description": "Leaf file name with an extension, such as chart.html. Not a directory and not a .dyson/temp/ path."
                    },
                    "content": {
                      "type": "string",
                      "description": "UTF-8 text to write. Capped at 512 KiB."
                    }
                  },
                  "required": ["path", "content"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ReadTempFile",
            Description =
                "Read a temporary file previously returned by WriteTempFile. path must be that exact workspace-relative path under .dyson/temp/. Refuses every other path. Returns JSON with path, content, and byteLength.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "path": {
                      "type": "string",
                      "description": "Exact workspace-relative path returned by WriteTempFile. A leaf under .dyson/temp/ with the random suffix."
                    }
                  },
                  "required": ["path"]
                }
                """,
        };
    }

    private static IEnumerable<DysonMcpTool> CreateMetaAgentDroneTools()
    {
        yield return new DysonMcpTool
        {
            Name = "ReadMetaPlan",
            Description =
                "Load a plan by planId (title + markdown). If your brief names a planId, read it before you start — " +
                "it is the authoritative brief and it is kept current.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "planId": { "type": "integer", "description": "Positive plan id." }
                  },
                  "required": ["planId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SubmitMetaPlan",
            Description =
                "Publish or revise a meta plan (stored in the database, not as a file). " +
                "Returns planId. Revise by calling again with the same planId. " +
                "This is not a report — still call SubmitSubagentReport after it succeeds.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "title": { "type": "string", "description": "Plan title." },
                    "markdown": { "type": "string", "description": "Plan body. Must be non-empty." },
                    "planId": { "type": "integer", "description": "Existing plan id to revise. Omit to create a new plan." },
                    "summary": { "type": "string", "description": "Optional one-line summary for the parent turn." }
                  },
                  "required": ["title", "markdown"]
                }
                """,
        };
    }
}

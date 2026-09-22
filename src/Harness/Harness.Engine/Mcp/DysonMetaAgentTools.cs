namespace DysonHarness;

/// <summary>
/// Meta Agent / Meta Agent Drone catalog: allowlist strip + meta-only tool definitions.
/// File, shell, browser, search, wait, and completion tools are structurally absent.
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

    /// <summary>Exact Meta Agent catalog (no file/shell/wait/completion tools).</summary>
    public static readonly IReadOnlyList<string> AllowedToolNames =
    [
        "BeginBuildPlan",
        "CompactConversation",
        "CreateAsyncMetaAgentDrone",
        "CreateTodo",
        "DeleteMetaAgent",
        "DeletePlan",
        "GetOpenRulesConfig",
        "ListMetaAgentDrones",
        "ListPlans",
        "ListTodos",
        "LoadSkill",
        "MessageMetaAgentDrone",
        "PostConversationMessage",
        "ReadMetaAgentDroneLog",
        "RemoveTodos",
        "RespondToSubagentEvent",
        "SetPlanStatus",
        "StartAsyncExploreAgent",
        "StopMetaAgentDrone",
        "SummarizeTurns",
        "UpdateTodo",
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
    /// Strips everything not in <see cref="AllowedToolNames"/>, restores shared schemas,
    /// then adds meta-only tools. Authoritative for Meta Agent mode.
    /// </summary>
    public static void ApplyAllowlist(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        foreach (var name in pipeline.Tools.Keys.ToArray())
        {
            if (!AllowedToolNameSet.Contains(name))
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
                "Spawn a Meta Agent Drone for anything that changes the repository (non-blocking). " +
                "Each drone gets its own git worktree and merges on completion. " +
                "Returns immediately with droneId / persistenceId / worktreeBranch; never waits. " +
                "purpose=build (default) implements; purpose=plan explores then SubmitMetaPlan. " +
                "Call ListMetaAgentDrones before dispatching. Reuse an existing drone with MessageMetaAgentDrone " +
                "instead of spawning a second one for the same work.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "task": { "type": "string", "description": "Assigned task brief for the drone. Goal, constraints, and acceptance criteria." },
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
                  "required": ["task"]
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

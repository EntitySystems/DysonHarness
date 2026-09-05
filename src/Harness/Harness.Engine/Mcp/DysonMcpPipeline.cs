using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Per-session MCP tool catalog. Format for prompt injection; live execution later.
/// Custom agent authors who want MCP-over-shell parity should include that preference in their own prompts.
/// </summary>
public sealed class DysonMcpPipeline
{
    /// <summary>
    /// Soft Plan-mode ShellExecute warning (catalog description + result preamble).
    /// Command still runs; this is prompt reinforcement only.
    /// </summary>
    public const string PlanShellExecuteWarning =
        "WARNING: Plan mode — ShellExecute is for read-only inspection only " +
        "(e.g. dir, git status, small type/Get-Content). " +
        "Never execute programs (dotnet run, builds, installs, servers). " +
        "Prefer ReadFile / Grep / ListDirectory.";

    public DysonMcpAccessMode AccessMode { get; }
    public Dictionary<string, DysonMcpTool> Tools { get; } = new(StringComparer.Ordinal);
    public DysonMcpAutoReviewProxy? AutoReviewProxy { get; }

    private readonly IReadOnlyList<string> _availableShellNames;
    private readonly bool _browserControlAvailable;

    private DysonMcpPipeline(
        DysonMcpAccessMode accessMode,
        IReadOnlyList<string> availableShellNames,
        bool browserControlAvailable)
    {
        AccessMode = accessMode;
        _availableShellNames = availableShellNames;
        _browserControlAvailable = browserControlAvailable;
        AutoReviewProxy = accessMode == DysonMcpAccessMode.AutoReview
            ? new DysonMcpAutoReviewProxy(this)
            : null;
    }

    public static DysonMcpPipeline CreateDefault(
        DysonMcpAccessMode accessMode,
        IReadOnlyList<string>? availableShellNames = null,
        bool browserControlAvailable = false,
        DysonUiThemeSnapshot? uiTheme = null)
    {
        availableShellNames ??= DysonShell.DefaultShellNamesForCurrentPlatform();
        var pipeline = new DysonMcpPipeline(accessMode, availableShellNames, browserControlAvailable);
        foreach (var tool in DefaultTools(availableShellNames, browserControlAvailable, uiTheme ?? DysonUiThemeSnapshot.Default))
            pipeline.Tools[tool.Name] = tool;
        return pipeline;
    }

    /// <summary>
    /// Replaces <c>RenderHtmlVisualization</c> from the current presentation snapshot.
    /// Description is <c>init</c>-only on <see cref="DysonMcpTool"/>, so replace the catalog entry.
    /// No-op when the tool is omitted (policy / catalog).
    /// </summary>
    public void ApplyVisualizationTheme(DysonUiThemeSnapshot uiTheme)
    {
        if (!Tools.ContainsKey("RenderHtmlVisualization"))
            return;
        Tools["RenderHtmlVisualization"] = CreateRenderHtmlVisualizationTool(uiTheme);
    }

    /// <summary>
    /// Rebuilds <c>ShellExecute</c> and long-running shell tools for the current agent mode.
    /// Description is <c>init</c>-only on <see cref="DysonMcpTool"/>, so replace catalog entries.
    /// </summary>
    public void ConfigureShellExecuteForMode(bool planMode)
    {
        var tool = CreateShellExecuteTool(_availableShellNames, planMode);
        if (tool is null)
            Tools.Remove("ShellExecute");
        else
            Tools["ShellExecute"] = tool;

        ConfigureLongRunningShellTools(planMode);
    }

    /// <summary>
    /// Adds/removes long-running shell tools (same platform gate as ShellExecute).
    /// Plan mode soft-warns on <c>StartLongRunningShell</c> only.
    /// </summary>
    public void ConfigureLongRunningShellTools(bool planMode)
    {
        if (_availableShellNames.Count == 0)
        {
            Tools.Remove("StartLongRunningShell");
            Tools.Remove("ListLongRunningShells");
            Tools.Remove("ReadLongRunningShellTail");
            Tools.Remove("AbortLongRunningShell");
            Tools.Remove("RequestLongRunningShellCancellation");
            Tools.Remove("LongRunningShellInteract");
            Tools.Remove("SubscribeToLongRunningShellCompletion");
            Tools.Remove("WaitForLongRunningShellCompletion");
            return;
        }

        foreach (var t in CreateLongRunningShellTools(_availableShellNames, planMode))
            Tools[t.Name] = t;
    }

    /// <summary>Prepends <see cref="PlanShellExecuteWarning"/> when <paramref name="planMode"/>.</summary>
    public static string PrefixPlanShellWarning(bool planMode, string content) =>
        planMode ? PlanShellExecuteWarning + "\n\n" + content : content;

    /// <summary>
    /// Builds ShellExecute with a shell enum matching the session's available types.
    /// Returns null when no shells are available for the platform.
    /// When <paramref name="planMode"/>, appends <see cref="PlanShellExecuteWarning"/> to Description.
    /// </summary>
    public static DysonMcpTool? CreateShellExecuteTool(
        IReadOnlyList<string> available,
        bool planMode = false)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0)
            return null;

        var names = available.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        if (names.Length == 0)
            return null;

        var listed = string.Join(", ", names);
        var enumJson = string.Join(", ", names.Select(n => $"\"{n}\""));
        var commandDescription = AppendPythonNodeCommandSchemaDescription(
            "Command line to execute in the chosen shell.", names);

        var description =
            "Run a command in the session work directory. " +
            $"Available shells for this session: {listed}. " +
            "You must pass shell as one of these. Prefer dedicated MCP file tools over shell when they fit.";
        description = AppendPythonNodeSnippetSentence(description, names);
        description +=
            " stdout/stderr are captured up to 64KiB each; overflow is truncated (command may still run until timeout).";
        if (planMode)
            description += " " + PlanShellExecuteWarning;

        return new DysonMcpTool
        {
            Name = "ShellExecute",
            Description = description,
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "shell": {
                      "type": "string",
                      "enum": [{{enumJson}}],
                      "description": "Shell to use (must be one of the available shells for this session)."
                    },
                    "command": {
                      "type": "string",
                      "description": "{{commandDescription}}"
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "description": "Optional max run time in milliseconds before the process is killed."
                    },
                    "workingDirectory": {
                      "type": "string",
                      "description": "Optional subdirectory under the work root (default: work root)."
                    }
                  },
                  "required": ["shell", "command"]
                }
                """,
        };
    }

    /// <summary>
    /// Long-running shell tools when the platform has available shells; empty otherwise.
    /// When <paramref name="planMode"/>, <c>StartLongRunningShell</c> description includes the Plan soft warning.
    /// </summary>
    public static IEnumerable<DysonMcpTool> CreateLongRunningShellTools(
        IReadOnlyList<string> available,
        bool planMode = false)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0)
            yield break;

        var names = available.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        if (names.Length == 0)
            yield break;

        var listed = string.Join(", ", names);
        var enumJson = string.Join(", ", names.Select(n => $"\"{n}\""));
        var commandDescription = AppendPythonNodeCommandSchemaDescription(
            "Command line to run in the background.", names);

        var startDescription =
            "Recommended for E2E test runs, large application builds, and keeping development servers running. " +
            "Start a background long-running shell in the session work directory. " +
            $"Available shells: {listed}. Returns longRunningShellId and the first ~1s of combined output. " +
            "Use ListLongRunningShells / ReadLongRunningShellTail / LongRunningShellInteract / " +
            "WaitForLongRunningShellCompletion / SubscribeToLongRunningShellCompletion (parent-only) / RequestLongRunningShellCancellation / AbortLongRunningShell to manage it. " +
            "Not persisted across UI restart (orphans OS processes). Prefer ShellExecute for one-shot commands.";
        startDescription = AppendPythonNodeSnippetSentence(startDescription, names);
        if (planMode)
            startDescription += " " + PlanShellExecuteWarning;

        yield return new DysonMcpTool
        {
            Name = "StartLongRunningShell",
            Description = startDescription,
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "shell": {
                      "type": "string",
                      "enum": [{{enumJson}}],
                      "description": "Shell to use (must be one of the available shells for this session)."
                    },
                    "command": {
                      "type": "string",
                      "description": "{{commandDescription}}"
                    },
                    "workingDirectory": {
                      "type": "string",
                      "description": "Optional subdirectory under the work root (default: work root)."
                    }
                  },
                  "required": ["shell", "command"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListLongRunningShells",
            Description =
                "List long-running shells for this session work directory " +
                "(id, status, shell, short command, exitCode if terminal, startedUtc).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ReadLongRunningShellTail",
            Description =
                "Read recent output from a long-running shell. " +
                "Optional timeoutMs > 0 waits for new output; default 0 returns immediately. " +
                "maxChars defaults to 8KiB and is clamped to 64KiB.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "longRunningShellId": {
                      "type": "integer",
                      "description": "Id returned by StartLongRunningShell."
                    },
                    "maxChars": {
                      "type": "integer",
                      "description": "Max characters of combined output to return (default 8KiB, clamped to 64KiB)."
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "description": "Optional wait for new output in milliseconds (default 0 = immediate)."
                    }
                  },
                  "required": ["longRunningShellId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "AbortLongRunningShell",
            Description =
                "Force-kill a long-running shell process tree (same as UI Force stop). " +
                "Waits until exited or timeoutMs (default 10000).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "longRunningShellId": {
                      "type": "integer",
                      "description": "Id returned by StartLongRunningShell."
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "description": "Max wait for exit in milliseconds (default 10000)."
                    }
                  },
                  "required": ["longRunningShellId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "RequestLongRunningShellCancellation",
            Description =
                "Soft-cancel a long-running shell (Ctrl+C on stdin, else CloseMainWindow). " +
                "Waits until exited or timeoutMs (default 10000). Prefer AbortLongRunningShell to force-kill.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "longRunningShellId": {
                      "type": "integer",
                      "description": "Id returned by StartLongRunningShell."
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "description": "Max wait for exit in milliseconds (default 10000)."
                    }
                  },
                  "required": ["longRunningShellId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "LongRunningShellInteract",
            Description =
                "Write a line to a long-running shell's stdin. timeoutMs waits for write/flush only (default 5000).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "longRunningShellId": {
                      "type": "integer",
                      "description": "Id returned by StartLongRunningShell."
                    },
                    "input": {
                      "type": "string",
                      "description": "Text to write to stdin (newline appended if missing)."
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "description": "Max wait for write/flush in milliseconds (default 5000)."
                    }
                  },
                  "required": ["longRunningShellId", "input"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SubscribeToLongRunningShellCompletion",
            Description =
                "Parent-only (root sessions). Subagents must use WaitForLongRunningShellCompletion. " +
                "Subscribe the current session to a one-shot ShellExited harness turn when a long-running shell " +
                "exits/aborts. Returns immediately (subscribed=true). If already terminal, fires once now. " +
                "Optional includeTailMaxChars (default 8000) caps the auto-read tail in that turn.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "longRunningShellId": {
                      "type": "integer",
                      "description": "Id returned by StartLongRunningShell."
                    },
                    "includeTailMaxChars": {
                      "type": "integer",
                      "description": "Max characters of auto-read tail for the ShellExited Instruction (default 8000, clamped to 64KiB)."
                    }
                  },
                  "required": ["longRunningShellId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WaitForLongRunningShellCompletion",
            Description =
                "Block until the shell is Exited/Aborted or timeoutMs elapses. " +
                "Already-terminal shells return immediately. Does not subscribe and does not queue ShellExited. " +
                "Prompt cancel / tool cancellation aborts the wait. Available to root and subagents.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "longRunningShellId": {
                      "type": "integer",
                      "description": "Id returned by StartLongRunningShell."
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "description": "Mandatory wait budget in milliseconds. Block until Exited/Aborted or this many ms. Must be > 0 (executor rejects <= 0)."
                    }
                  },
                  "required": ["longRunningShellId", "timeoutMs"]
                }
                """,
        };
    }

    /// <summary>
    /// When the session enum includes Python and/or Node, remind the model those commands are snippets.
    /// </summary>
    private static string AppendPythonNodeSnippetSentence(string description, IReadOnlyList<string> names)
    {
        var hasPython = names.Any(n => n.Equals("Python", StringComparison.OrdinalIgnoreCase));
        var hasNode = names.Any(n => n.Equals("Node", StringComparison.OrdinalIgnoreCase));
        if (!hasPython && !hasNode)
            return description;

        if (hasPython && hasNode)
            return description + " When shell is Python, command is a raw Python snippet (passed to `-c`), not a file path or shell command line. When shell is Node, command is a raw JavaScript snippet (passed to `-e`), not a file path or shell command line.";
        if (hasPython)
            return description + " When shell is Python, command is a raw Python snippet (passed to `-c`), not a file path or shell command line.";
        return description + " When shell is Node, command is a raw JavaScript snippet (passed to `-e`), not a file path or shell command line.";
    }

    /// <summary>
    /// Appends Python/Node snippet clauses to the <c>command</c> schema description when those shells are present.
    /// </summary>
    private static string AppendPythonNodeCommandSchemaDescription(string baseDescription, IReadOnlyList<string> names)
    {
        var hasPython = names.Any(n => n.Equals("Python", StringComparison.OrdinalIgnoreCase));
        var hasNode = names.Any(n => n.Equals("Node", StringComparison.OrdinalIgnoreCase));
        if (hasPython)
            baseDescription += " For Python, pass a raw Python snippet (`-c`).";
        if (hasNode)
            baseDescription += " For Node, pass a raw JavaScript snippet (`-e`).";
        return baseDescription;
    }

    /// <summary>Formats the tools dictionary into a prompt-injectable catalog string.</summary>
    public string FormatToolsForPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Available MCP tools");
        sb.AppendLine(
            "Every tool call must include harness fields: callId (optional; assigned if omitted), stage (int; required). " +
            "Calls are ordered by stage; same stage runs concurrently.");
        sb.AppendLine();

        foreach (var tool in Tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append("### ");
            sb.AppendLine(tool.Name);
            sb.AppendLine(tool.Description);
            sb.AppendLine("Input schema:");
            sb.AppendLine(tool.InputSchemaJson);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Layer catalog gating for inter-agent + Ask tools.
    /// Depth 0 = root, 1 = L1 child, 2+ = deeper.
    /// </summary>
    public void ConfigureInterAgentTools(int depth)
    {
        // Ensure catalog tools exist (DefaultTools already added them; restore if removed).
        EnsureInterAgentToolsPresent();

        if (depth <= 0)
        {
            Tools.Remove("AskQuestionFromParent");
            Tools.Remove("PromptUserDialogFromParent");
            Tools.Remove("TriggerParentEvent");
            // AskQuestion, PromptUserDialog, RespondToSubagentEvent, TriggerSubagentEvent stay
            return;
        }

        Tools.Remove("AskQuestion");
        Tools.Remove("PromptUserDialog");
        Tools.Remove("SubscribeToLongRunningShellCompletion");

        if (depth == 1)
        {
            // AskQuestionFromParent, PromptUserDialogFromParent, TriggerParentEvent, Respond, TriggerSubagentEvent stay
            return;
        }

        // Deeper: no AskQuestionFromParent / PromptUserDialogFromParent
        Tools.Remove("AskQuestionFromParent");
        Tools.Remove("PromptUserDialogFromParent");
    }

    private void EnsureInterAgentToolsPresent()
    {
        foreach (var tool in InterAgentTools())
        {
            if (!Tools.ContainsKey(tool.Name))
                Tools[tool.Name] = tool;
        }
    }

    private static IEnumerable<DysonMcpTool> InterAgentTools()
    {
        var questionsSchema = DysonAskQuestion.SharedQuestionsSchemaJson();
        var dialogSchema = DysonPromptUserDialog.SharedDialogSchemaJson();

        yield return new DysonMcpTool
        {
            Name = "AskQuestion",
            Description =
                "Root-only: ask the user 1–8 clarifying / design questions via the composer UI and block until answered. " +
                "Use for open-ended clarification — not for picking among concrete next actions (use PromptUserDialog for that). " +
                "Per-question Skip is allowed; allowMultiple permits multi-select; custom answers always allowed. " +
                "Result is a Q#/A# text block.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "questions": {{questionsSchema}}
                  },
                  "required": ["questions"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "AskQuestionFromParent",
            Description =
                "L1 subagent only: ask the parent user clarifying / design questions (wraps TriggerParentEvent kind=askQuestion). " +
                "Blocks until the user answers in the Auto UI. Same questions schema as AskQuestion. " +
                "For concrete action choices use PromptUserDialogFromParent instead.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "questions": {{questionsSchema}}
                  },
                  "required": ["questions"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "PromptUserDialog",
            Description =
                "Root-only: show a modal action picker (title, description, 1–4 actions) and block until the user chooses. " +
                "Use when a future step is unclear and you need a quick choice among concrete options. " +
                "Do not use for open-ended design clarification (use AskQuestion). " +
                "UI always adds a non-primary Skip. Result JSON: { action, skipped } (Skip includes guidance).",
            InputSchemaJson = dialogSchema,
        };

        yield return new DysonMcpTool
        {
            Name = "PromptUserDialogFromParent",
            Description =
                "L1 subagent only: modal action picker for the parent user (wraps TriggerParentEvent kind=promptUserDialog). " +
                "Blocks until the user chooses in the Auto UI. Same schema as PromptUserDialog. " +
                "For clarifying / design questions use AskQuestionFromParent instead.",
            InputSchemaJson = dialogSchema,
        };

        yield return new DysonMcpTool
        {
            Name = "TriggerParentEvent",
            Description =
                "Subagent → parent: queue an event and block until the parent calls RespondToSubagentEvent. " +
                "Fails immediately if the parent is inside WaitForSubagent for any child (deadlock guard). " +
                "Prefer SubmitSubagentReport for final handoff; use this for mid-run coordination. " +
                "For agent-to-agent text use kind like message or status (parent gets a harness continuation turn). " +
                "askQuestion opens Ask UI only when payload is the AskQuestion questions schema — prefer AskQuestionFromParent; " +
                "promptUserDialog opens the action modal when payload is the PromptUserDialog schema — prefer PromptUserDialogFromParent; " +
                "plain-text askQuestion / promptUserDialog is treated like other kinds (parent auto-turn).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "kind": {
                      "type": "string",
                      "description": "Event kind. Use message/status for agent-to-agent text. askQuestion / promptUserDialog only with their schemas (prefer AskQuestionFromParent / PromptUserDialogFromParent)."
                    },
                    "payload": {
                      "type": "string",
                      "description": "Event payload (JSON or text) for the parent. For askQuestion / promptUserDialog, must match the tool schema to open host UI."
                    }
                  },
                  "required": ["kind", "payload"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "RespondToSubagentEvent",
            Description =
                "Parent: complete a pending child event from TriggerParentEvent / AskQuestionFromParent / PromptUserDialogFromParent. " +
                "Always allowed for a matching pending eventId even while WaitForSubagent is in progress.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "subagentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Child subagent id that triggered the event."
                    },
                    "eventId": {
                      "type": "string",
                      "description": "Guid eventId from the harness continuation / Subagent event block."
                    },
                    "reply": {
                      "type": "string",
                      "description": "Reply payload returned to the child’s blocked tool call."
                    }
                  },
                  "required": ["subagentId", "eventId", "reply"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "TriggerSubagentEvent",
            Description =
                "Parent → child: inject a prompt. Default (interruptSubagent=false) queues for the child’s next turn. " +
                "interruptSubagent=true cancels the in-flight child turn (and any pending parent-event wait) and runs the payload immediately. " +
                "Fails without interrupt when the child is awaiting a parent-event reply. Returns quickly (queued vs interrupted). " +
                "Injecting into a child that already submitted a report reopens it (`Active`) so SubmitSubagentReport works again.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "subagentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Id of the child to inject into."
                    },
                    "payload": {
                      "type": "string",
                      "description": "Prompt/instructions injected into the child."
                    },
                    "interruptSubagent": {
                      "type": "boolean",
                      "description": "If true, cancel in-flight child turn (and any pending parent-event wait) and run payload immediately. Default false = enqueue for next turn."
                    }
                  },
                  "required": ["subagentId", "payload"]
                }
                """,
        };
    }

    /// <summary>
    /// Browser MCP tools when <see cref="DysonAgentSessionConfig.BrowserControl"/> is registered.
    /// </summary>
    public static IEnumerable<DysonMcpTool> CreateBrowserTools()
    {
        const string TimeoutMsJson =
            """
            "timeoutMs": { "type": "integer", "description": "Optional timeout in milliseconds (default 60000)." }
            """;

        yield return new DysonMcpTool
        {
            Name = "OpenBrowser",
            Description =
                "Open a new agent browser window (Windows CefSharp WPF chrome). " +
                "Optional url, width, height. Returns windowId and initial tabId.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "Optional initial URL." },
                    "width": { "type": "integer", "description": "Optional window width (default 1280)." },
                    "height": { "type": "integer", "description": "Optional window height (default 800)." },
                    {{TimeoutMsJson}}
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListBrowserWindows",
            Description = "List open agent browser windows (windowId).",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    {{TimeoutMsJson}}
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CloseBrowser",
            Description = "Close an agent browser window by windowId.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string", "description": "Window id from OpenBrowser / ListBrowserWindows." },
                    {{TimeoutMsJson}}
                  },
                  "required": ["windowId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ResizeBrowser",
            Description = "Resize an agent browser window (ResizeWebView).",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "width": { "type": "integer" },
                    "height": { "type": "integer" },
                    {{TimeoutMsJson}}
                  },
                  "required": ["windowId", "width", "height"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListBrowserTabs",
            Description = "List tabs in a browser window.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    {{TimeoutMsJson}}
                  },
                  "required": ["windowId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "NewBrowserTab",
            Description = "Open a new tab in a browser window. Optional url.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "url": { "type": "string" },
                    {{TimeoutMsJson}}
                  },
                  "required": ["windowId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CloseBrowserTab",
            Description = "Close a tab. Closing the last tab closes the window.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    {{TimeoutMsJson}}
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ActivateBrowserTab",
            Description = "Activate (focus) a tab in a browser window.",
            InputSchemaJson = $$"""
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    {{TimeoutMsJson}}
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserNavigate",
            Description = "Navigate a tab to a URL.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "url": { "type": "string" }
                  },
                  "required": ["windowId", "tabId", "url"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserGoBack",
            Description = "Navigate back in a tab (PopNavigation).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserGoForward",
            Description = "Navigate forward in a tab.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserReload",
            Description = "Reload the current page in a tab.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ClearBrowserCache",
            Description =
                "Clear the shared CEF HTTP cache for open agent browser windows, then hard-reload every tab. " +
                "Does not clear cookies or site storage. No args — always all open windows. " +
                "Empty window list returns success with windows=0, tabsReloaded=0. " +
                "Agent windows and the Windows shell UI share %LocalAppData%\\DysonHarness\\cef-cache; " +
                "CDP cache clear is profile-wide (shell is not hard-reloaded).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserClick",
            Description =
                "Click in a tab via selector and/or x/y coordinates. Optional button (left|middle|right) and modifiers.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "selector": { "type": "string" },
                    "x": { "type": "number" },
                    "y": { "type": "number" },
                    "button": { "type": "string", "enum": ["left", "middle", "right"] },
                    "ctrlKey": { "type": "boolean" },
                    "shiftKey": { "type": "boolean" },
                    "altKey": { "type": "boolean" },
                    "metaKey": { "type": "boolean" },
                    "timeoutMs": { "type": "integer" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserType",
            Description = "Type text into a selector or the focused element. Optional clearFirst.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "text": { "type": "string" },
                    "selector": { "type": "string" },
                    "clearFirst": { "type": "boolean" },
                    "delayMs": { "type": "integer" },
                    "timeoutMs": { "type": "integer" }
                  },
                  "required": ["windowId", "tabId", "text"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserFill",
            Description = "Clear and fill an input matching selector.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "selector": { "type": "string" },
                    "value": { "type": "string" }
                  },
                  "required": ["windowId", "tabId", "selector", "value"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserHover",
            Description = "Hover a selector in a tab.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "selector": { "type": "string" }
                  },
                  "required": ["windowId", "tabId", "selector"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserPressKey",
            Description = "Press a key (optionally targeting a selector) with modifiers.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "key": { "type": "string" },
                    "selector": { "type": "string" },
                    "ctrlKey": { "type": "boolean" },
                    "shiftKey": { "type": "boolean" },
                    "altKey": { "type": "boolean" },
                    "metaKey": { "type": "boolean" },
                    "timeoutMs": { "type": "integer" }
                  },
                  "required": ["windowId", "tabId", "key"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserWaitForSelector",
            Description = "Wait until a CSS selector matches in the tab.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "selector": { "type": "string" },
                    "timeoutMs": { "type": "integer" }
                  },
                  "required": ["windowId", "tabId", "selector"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserWaitForNavigation",
            Description = "Wait for the next navigation/load to finish in a tab.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "timeoutMs": { "type": "integer" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserExecuteJavaScript",
            Description = "Evaluate JavaScript in the tab and return the result as text.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "code": { "type": "string" }
                  },
                  "required": ["windowId", "tabId", "code"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserGetHtml",
            Description = "Return document.documentElement.outerHTML for a tab.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserTakeScreenshot",
            Description =
                "Capture a screenshot of the tab (JPEG multimodal attachment + short JSON ack; no base64 in Content). " +
                "Requires FileStorage (presigned HTTPS URL); unconfigured returns file_storage_required. " +
                "Optional timeoutMs (default 60000).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" },
                    "timeoutMs": { "type": "integer" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserReadConsoleLog",
            Description = "Read collected console messages for a tab (thin collector until CDP deepens).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "BrowserReadNetworkLog",
            Description =
                "Read collected network entries for a tab (main-frame loads only until CDP request logging).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "windowId": { "type": "string" },
                    "tabId": { "type": "string" }
                  },
                  "required": ["windowId", "tabId"]
                }
                """,
        };
    }

    private const string BrowserTimeoutMsDescription =
        "Optional timeout in milliseconds (default 60000).";

    /// <summary>
    /// Injects optional <c>timeoutMs</c> (default 60000) into every browser tool schema.
    /// </summary>
    private static DysonMcpTool EnsureOptionalBrowserTimeoutMs(DysonMcpTool tool)
    {
        using var doc = JsonDocument.Parse(tool.InputSchemaJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            var wroteProperties = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("properties"))
                {
                    wroteProperties = true;
                    writer.WritePropertyName("properties");
                    writer.WriteStartObject();
                    foreach (var p in prop.Value.EnumerateObject())
                    {
                        if (p.NameEquals("timeoutMs"))
                            continue;
                        p.WriteTo(writer);
                    }

                    writer.WritePropertyName("timeoutMs");
                    writer.WriteStartObject();
                    writer.WriteString("type", "integer");
                    writer.WriteString("description", BrowserTimeoutMsDescription);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!wroteProperties)
            {
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                writer.WritePropertyName("timeoutMs");
                writer.WriteStartObject();
                writer.WriteString("type", "integer");
                writer.WriteString("description", BrowserTimeoutMsDescription);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return new DysonMcpTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchemaJson = Encoding.UTF8.GetString(stream.ToArray()),
        };
    }

    private static DysonMcpTool CreateRenderHtmlVisualizationTool(DysonUiThemeSnapshot uiTheme) =>
        new()
        {
            Name = "RenderHtmlVisualization",
            Description =
                "Render a self-contained HTML + CSS + JavaScript mini-component as a visualization in a sandboxed iframe in the current turn, and add it to the session’s Visualizations picker. " +
                "This tool is highly encouraged whenever the user asks to see a visual preview or result, graph, chart, diagram, dashboard, animation, UI mock-up, or other visual output. " +
                DysonAgentSystemPrompts.FormatVisualizationThemeGuidance(uiTheme) + " " +
                "title, html, css, and js are required. For each of the three asset arguments, provide exactly one source: raw content, or tempFile. " +
                "A tempFile must be the exact workspace-relative path returned by CreateFile with isTempFile: true, must remain under .dyson/temp/, and must use the matching extension; never pass an arbitrary workspace file or invent a temp path. " +
                "Use CreateFile(isTempFile: true) first for large or multiline assets and place the render call in a later harness stage. " +
                "The component must be self-contained: network requests, external scripts/styles/fonts, parent-page access, filesystem access, popups, forms, downloads, and top-level navigation are blocked. " +
                "Use native browser APIs, inline SVG, Canvas, and data/blob images instead of external dependencies.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "title": {
                      "type": "string",
                      "description": "Required user-facing visualization title shown inline and in the Visualizations modal. Maximum 120 characters."
                    },
                    "html": {
                      "type": "object",
                      "description": "HTML body markup. Provide exactly one of content or tempFile.",
                      "properties": {
                        "content": { "type": "string", "description": "Raw HTML body markup. Keep CSS and JavaScript in their separate arguments." },
                        "tempFile": { "type": "string", "description": "Exact .dyson/temp/*.html or *.htm path returned by CreateFile with isTempFile true." }
                      },
                      "oneOf": [{ "required": ["content"] }, { "required": ["tempFile"] }],
                      "additionalProperties": false
                    },
                    "css": {
                      "type": "object",
                      "description": "Stylesheet source. Provide exactly one of content or tempFile; raw content may be an empty string.",
                      "properties": {
                        "content": { "type": "string", "description": "Raw CSS stylesheet content." },
                        "tempFile": { "type": "string", "description": "Exact .dyson/temp/*.css path returned by CreateFile with isTempFile true." }
                      },
                      "oneOf": [{ "required": ["content"] }, { "required": ["tempFile"] }],
                      "additionalProperties": false
                    },
                    "js": {
                      "type": "object",
                      "description": "JavaScript source executed after the HTML and CSS are installed. Provide exactly one of content or tempFile; raw content may be an empty string.",
                      "properties": {
                        "content": { "type": "string", "description": "Raw JavaScript content." },
                        "tempFile": { "type": "string", "description": "Exact .dyson/temp/*.js or *.mjs path returned by CreateFile with isTempFile true." }
                      },
                      "oneOf": [{ "required": ["content"] }, { "required": ["tempFile"] }],
                      "additionalProperties": false
                    }
                  },
                  "required": ["title", "html", "css", "js"],
                  "additionalProperties": false
                }
                """,
        };

    private static DysonMcpTool CreateGenerateImageTool() =>
        new()
        {
            Name = "GenerateImage",
            Description =
                "Generate one or more images from a text prompt using the session's configured image-generation model. " +
                "Generated images are written as PNG artifacts beneath `.dyson/image-gen/`, returned as compact metadata acknowledgements (never base64), and rendered by the application. " +
                "Use this only when the user asks to create or generate an image. " +
                "prompt is required; size, quality, style, background, outputFormat, and count are optional generation settings. " +
                "The configured model is selected by the application and cannot be overridden by this tool.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "prompt": {
                      "type": "string",
                      "description": "Required description of the image to generate. Be specific about subject, composition, lighting, style, and any text that must appear."
                    },
                    "size": {
                      "type": "string",
                      "description": "Optional requested image dimensions, such as 1024x1024, 1536x1024, 1024x1536, or auto."
                    },
                    "quality": {
                      "type": "string",
                      "description": "Optional generation quality, such as low, medium, high, or auto."
                    },
                    "style": {
                      "type": "string",
                      "description": "Optional rendering style, such as vivid or natural when supported by the configured model."
                    },
                    "background": {
                      "type": "string",
                      "description": "Optional background preference, such as transparent, opaque, or auto when supported by the configured model."
                    },
                    "outputFormat": {
                      "type": "string",
                      "description": "Optional provider output format preference: png, jpeg, or webp. Saved artifacts are always normalized to PNG."
                    },
                    "count": {
                      "type": "integer",
                      "minimum": 1,
                      "maximum": 10,
                      "description": "Optional number of images to generate. Defaults to one."
                    }
                  },
                  "required": ["prompt"],
                  "additionalProperties": false
                }
                """,
        };

    private static IEnumerable<DysonMcpTool> DefaultTools(
        IReadOnlyList<string> availableShellNames,
        bool browserControlAvailable,
        DysonUiThemeSnapshot uiTheme)
    {
        foreach (var tool in InterAgentTools())
            yield return tool;

        if (browserControlAvailable)
        {
            foreach (var tool in CreateBrowserTools())
                yield return EnsureOptionalBrowserTimeoutMs(tool);
        }

        yield return new DysonMcpTool
        {
            Name = "StartSubagent",
            Description =
                "Spawn a nested agent session for delegated work (non-blocking). " +
                "Returns immediately with subagentId / persistenceId; the child runs in the background. " +
                "When the child calls SubmitSubagentReport, the parent is notified and the host queues a turn. " +
                "Do not WaitForSubagent on Drones. In Work (and Drone), any Explore you start is a blocker: WaitForSubagent on a later stage of the same turn before further parent work. In Plan, Wait only when that Explore blocks the next automatic turn. " +
                "Optional todos seeds the child’s own session todo list. " +
                "Optional contextFiles preloads work-relative files into the child’s first turn as File context (path visible as `[File: relative/path]` before contents). The caller is encouraged to share relevant files so the subagent does not need to load them manually. " +
                "Optional modelSlug picks a different model (slug or display alias; omit to inherit parent). " +
                "Optional reasoningEffort overrides the child’s reasoning_effort (omit/null → chosen slug’s defaultEffort; when inheriting parent model, omit keeps the parent’s current effort). " +
                "Cannot spawn a child whose agentMode is Plan (Plan is top-level only). A Plan parent may StartSubagent Explore. Explore parents cannot spawn. Drone may spawn Explore only (not another Drone).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "agentMode": { "type": "string", "description": "Mode for the sub-agent (e.g. Explore, Drone). Not Plan." },
                    "task": { "type": "string", "description": "Assigned task brief for the sub-agent." },
                    "context": { "type": "string", "description": "Optional extra context or constraints." },
                    "modelSlug": {
                      "type": "string",
                      "description": "Optional model slug or display alias. Omit to inherit the parent session model. Same provider kind only."
                    },
                    "reasoningEffort": {
                      "type": "string",
                      "description": "Optional freeform reasoning_effort for the child. Omit/null → slug defaultEffort when modelSlug is set; when inheriting the parent model, omit keeps the parent’s current effort."
                    },
                    "todos": {
                      "type": "array",
                      "description": "Optional seed checklist for the child’s session todo list.",
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
                    "contextFiles": {
                      "type": "array",
                      "description": "Optional work-relative file paths to preload into the child’s first turn as File context (`[File: path]` then contents). Encouraged: share relevant files so the subagent does not need to load them manually.",
                      "items": { "type": "string", "description": "Work-relative or workspace file path." }
                    }
                  },
                  "required": ["agentMode", "task"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListTodos",
            Description =
                "List todos for the current session (JSON array). " +
                "Each session (root or subagent) owns its own list. " +
                "Call this before SubmitSubagentReport to check for pending or ongoing work.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CreateTodo",
            Description =
                "Create a todo on the current session’s list. TaskCode must be unique within the session.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "displayName": { "type": "string", "description": "Human-readable todo title." },
                    "taskCode": { "type": "string", "description": "Stable code unique within this session." },
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
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "UpdateTodo",
            Description =
                "Update a todo on the current session by taskCode. " +
                "Optional fields patch displayName / status; comments replaces the full list; appendComment adds one comment.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "taskCode": { "type": "string", "description": "Todo to update." },
                    "displayName": { "type": "string", "description": "Optional new display name." },
                    "status": {
                      "type": "string",
                      "enum": ["pending", "ongoing", "complete"],
                      "description": "Optional new status."
                    },
                    "comments": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Optional full replace of the comments list."
                    },
                    "appendComment": {
                      "type": "string",
                      "description": "Optional comment to append (after any replace)."
                    }
                  },
                  "required": ["taskCode"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "DeleteTodo",
            Description = "Delete a todo from the current session by taskCode.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "taskCode": { "type": "string", "description": "Todo to delete." }
                  },
                  "required": ["taskCode"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListSubagents",
            Description =
                "List this session’s direct child subagents (session-owned roster). " +
                "Returns a JSON array with subagentId, persistenceId, agentMode, title, status, and optional modelLabel. " +
                "Use before WaitForSubagent / InspectSubagentLog / StopSubagent when the id is not in recent context " +
                "(e.g. after resume or compacted older StartSubagent results).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WaitForSubagent",
            Description =
                "Block until this subagent finishes (completed / failed / stopped) or timeoutMs. " +
                "Default timeout is 300000 ms (5 minutes) when timeoutMs is omitted. " +
                "In Work (and Drone), Wait immediately after starting an Explore — do not continue parent work until the report returns. " +
                "After launching a Drone, do not Wait; the harness queues a parent turn when SubmitSubagentReport arrives. " +
                "In Plan, Wait only when that Explore blocks the next automatic turn.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "subagentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Id of the subagent to wait on (≥ 1)."
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "description": "Optional max wait in milliseconds before returning. Default 300000 (5 minutes) when omitted."
                    }
                  },
                  "required": ["subagentId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "InspectSubagentLog",
            Description =
                "Read recent log lines for a running or finished subagent by Id. " +
                "Use for progress checks; do not busy-poll in a tight loop.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "subagentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Id of the subagent whose log to inspect (≥ 1)."
                    },
                    "maxLines": {
                      "type": "integer",
                      "description": "Optional max number of recent log lines to return."
                    }
                  },
                  "required": ["subagentId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "StopSubagent",
            Description =
                "Cancel a running subagent (cooperative stop via its run CancellationToken). " +
                "Marks the child Stopped and notifies the parent.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "subagentId": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Id of the subagent to stop (≥ 1)."
                    },
                    "reason": {
                      "type": "string",
                      "description": "Optional reason for the stop request."
                    }
                  },
                  "required": ["subagentId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SubmitSubagentReport",
            Description =
                "Subagents must call this when finished (or blocked). " +
                "Summary may be a success handoff or, when status is failed, a concrete failure reason. " +
                "Notifies the parent with the summary so the host can queue a parent turn. " +
                "All session todos must be Complete before a successful (completed) report; " +
                "failed reports may leave todos incomplete. " +
                "Before submitting: call ListTodos first to see if this session has any pending work; " +
                "if ListTodos shows pending or ongoing items, complete those via UpdateTodo first, then submit. " +
                "A successful submit ends this turn — do not call any more tools after it succeeds. " +
                "Further SubmitSubagentReport calls fail until a new child turn starts " +
                "(parent TriggerSubagentEvent, harness ShellExited, or any other child PromptHarnessTurnAsync). " +
                "Same-turn retries still fail. " +
                "Keep TriggerParentEvent only as the same-turn mid-run path after a successful submit. " +
                "If submit fails because a report already landed, call TriggerParentEvent (same-turn mid-run parent coordination, not a new report cycle). After TriggerParentEvent, do not call any more tools; end the turn. " +
                "If SubmitSubagentReport fails twice in the same turn, the harness auto-submits the last parseable report, cancels the child's current run, and ends the child so the model loop cannot issue another provider request. Parent TriggerSubagentEvent can still reopen the child for new work.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "summary": {
                      "type": "string",
                      "description": "Crisp handoff for the parent (findings, outcome) or a concrete failure reason when status is failed."
                    },
                    "status": {
                      "type": "string",
                      "enum": ["completed", "failed"],
                      "description": "Report outcome (default: completed)."
                    }
                  },
                  "required": ["summary"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CompleteTask",
            Description =
                "Request completion review: the harness schedules a confirmation turn rather than ending immediately. " +
                "On that follow-up turn you must call ConfirmTaskComplete or ContinueWork. " +
                "After ConfirmTaskComplete, a ReportSummary turn follows. " +
                "After a confirmed cycle, CompleteTask is valid again only after a new user/in-flight prompt " +
                "(Completed/Failed reopens to Active). " +
                "Do not call CompleteTask while still Completed with no new turn. Stopped/Interrupted stay locked.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "summary": { "type": "string", "description": "What was completed and how it was verified." },
                    "filesTouched": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Optional list of paths changed."
                    },
                    "residualRisks": { "type": "string", "description": "Optional leftover risks or follow-ups." }
                  },
                  "required": ["summary"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ConfirmTaskComplete",
            Description =
                "Affirm the prior CompleteTask claim after self-check. " +
                "The harness then schedules a ReportSummary turn (final handoff for this agent).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "rationale": {
                      "type": "string",
                      "description": "Optional short rationale that completion is genuinely satisfied."
                    }
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ContinueWork",
            Description =
                "Reject the prior CompleteTask claim and request a continuation turn for unfinished work.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "reason": {
                      "type": "string",
                      "description": "Optional why completion was withdrawn."
                    },
                    "remainingWork": {
                      "type": "string",
                      "description": "Optional description of what still needs to be done."
                    }
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ResumeCurrentTask",
            Description =
                "After a tool-round soft-pause rethink turn, continue the unfinished task with a fresh tool-round budget. " +
                "Only valid during a RethinkToolUsage turn. Provide rationale and/or continuationInstructions.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "rationale": {
                      "type": "string",
                      "description": "Optional why continuing is justified (not a doom loop)."
                    },
                    "continuationInstructions": {
                      "type": "string",
                      "description": "Optional brief guidance for the next Normal turn."
                    }
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WaitForSeconds",
            Description =
                "Block until the given number of seconds elapses (1–300). " +
                "Use for short deliberate delays; prompt cancel aborts the wait.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "seconds": {
                      "type": "integer",
                      "minimum": 1,
                      "maximum": 300,
                      "description": "Seconds to wait (1–300)."
                    }
                  },
                  "required": ["seconds"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "JsonDynamicStructuredLanguageToolchain",
            Description =
                "Interpret a nested JSON dynamic structured-language program that chains existing session MCP tools. " +
                "Strict nested FunctionCall/Loop only (no flat FunctionCall strings). " +
                "Branches on nested tool IsError via OnSuccess/OnFailure; optional ContinueWith and Loop. " +
                "JDSL-only intrinsic JDSL:ReturnOutput (required Arguments.output) stops the program and surfaces " +
                "that value as the model-facing tool result (full flow envelope still persisted for UI). " +
                "Argument refs: fromArg:name, fromResult:$0, fromResult:json.path. " +
                "Caps: nesting depth 8, 50 nested invocations, MaxIterations 1–20 (default 5). " +
                "Cannot call itself. Prefer LoadSkill(name: \"JDSL\", loadIndexOnly: true) for the agent guide; see Resources/Skills/JDSL.md.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "program": {
                      "description": "Toolchain program (object) or JSON string of the program.",
                      "oneOf": [{ "type": "object" }, { "type": "string" }]
                    }
                  },
                  "required": ["program"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ExpandThoughtProcess",
            Description =
                "Queue an ExpandThoughtProcess reformulation turn, hard-end the current turn, then auto-continue with a Normal turn. " +
                "Use when context is noisy or the plan is unclear. Optional focus clarifies what to reformulate. " +
                "Prefer SummarizeTurns for verbose-but-useful older turns; DropTurnContext for true noise only.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "focus": {
                      "type": "string",
                      "description": "Optional focus: what to clarify or reformulate."
                    }
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "StartNewTurn",
            Description =
                "Hard-end the current turn and queue a Normal follow-up whose Instruction is promptInstructions. " +
                "Use when you need a clean new turn with specific instructions (e.g. continue a multi-part reply). " +
                "Not a substitute for ExpandThoughtProcess (reformulation). Callable anytime.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "promptInstructions": {
                      "type": "string",
                      "description": "Non-empty instruction for the next Normal turn."
                    }
                  },
                  "required": ["promptInstructions"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SummarizeTurns",
            Description =
                "Compress listed turn ids (from [turnId=…] history headers) into compact context stubs via a harness worker. " +
                "Callable anytime. Requires reason. Prefer over DropTurnContext when useful facts remain. " +
                "Re-summarize overwrites an existing stub. Does not delete turns.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "turnIds": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Turn Guids from [turnId=…] transcript headers to summarize for future model context."
                    },
                    "reason": {
                      "type": "string",
                      "description": "Why these turns should be summarized for future model context."
                    }
                  },
                  "required": ["turnIds", "reason"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "DropTurnContext",
            Description =
                "Exclude listed turn ids (from [turnId=…] history headers) from future provider transcripts. " +
                "Callable anytime. Requires reason. Prefer SummarizeTurns when useful facts remain; use Drop for true noise only. " +
                "Does not delete turns; RestoreTurnContext or UI can restore. Prefer keep when unsure.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "turnIds": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Turn Guids from [turnId=…] transcript headers to exclude from future model context."
                    },
                    "reason": {
                      "type": "string",
                      "description": "Why these turns should be excluded from future model context."
                    }
                  },
                  "required": ["turnIds", "reason"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "RestoreTurnContext",
            Description =
                "Re-include previously dropped turn ids in future provider transcripts (undo DropTurnContext). " +
                "Callable anytime. Requires reason. Prefer keep when unsure; do not restore casually.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "turnIds": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Turn Guids from [turnId=…] transcript headers to re-include in future model context."
                    },
                    "reason": {
                      "type": "string",
                      "description": "Why these turns should be restored into future model context."
                    }
                  },
                  "required": ["turnIds", "reason"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SubmitPlan",
            Description =
                "Plan mode only: publish a plan artifact under .dyson/plans/{title}-{sha1}.md. " +
                "Writes the markdown file, appends a PlanResult turn, and returns planPath for later WriteFile updates. " +
                "Does not implement product code. Call once when the plan is ready; revise via WriteFile on planPath unless the user asks for a new plan.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "title": {
                      "type": "string",
                      "description": "Short plan title (used for the filename slug)."
                    },
                    "markdown": {
                      "type": "string",
                      "description": "Full plan markdown body to write."
                    }
                  },
                  "required": ["title", "markdown"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "RenameSession",
            Description =
                "Rename the current agent session for the UI/session list. " +
                "Call only when the harness rename-review mandate asks you to decide, " +
                "or when the user explicitly asks to rename.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "title": {
                      "type": "string",
                      "description": "New session title (non-empty after trim; max 120 characters)."
                    }
                  },
                  "required": ["title"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "GetDateTime",
            Description =
                "Return the current date and time. Use when the task needs an exact clock " +
                "(deadlines, \"today\", scheduling). Pass timezone: \"local\" for the host machine's local zone; " +
                "default \"utc\" for UTC. Do not guess the time from training data.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "timezone": {
                      "type": "string",
                      "enum": ["utc", "local"],
                      "description": "Clock zone: utc (default) or local (host machine)."
                    }
                  }
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "GetOpenRulesConfig",
            Description =
                "Return a JSON summary of work-root openrules.json (Root + Rules/Skills Path/Mode/Description/exists/isUrl/Providers). " +
                "No file bodies. Returns all manifest rows (no provider filter). Missing manifest notes the implicit AGENTS.md Root when present. " +
                "Use to discover AgentOptional entries for LoadSkill; AutoInclude content is already in the system prompt.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "InitializeOpenRules",
            Description =
                "Ensure work-root openrules.json exists. If missing, create a default document " +
                "(Root AGENTS.md, empty Rules, and the EntitySystems openrules AgentOptional SKILL.md URL). " +
                "If present, leave it unchanged. Returns JSON { created, openrules } with the file contents.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "LoadSkill",
            Description =
                "Load an agent skill into the current turn (and return its markdown). " +
                "Resolve order: (1) included Resources/Skills by file name or stem (e.g. JDSL / JDSL.md), " +
                "(2) work-root .dyson/skills/{name}/ (or .dyson/skills/{name}), " +
                "(3) literal work-relative path (file or directory), " +
                "(4) openrules.json AgentOptional Rules/Skills (by relative path, stem, URL, or catalog name; http(s) Path fetched). " +
                "Entries with a non-empty Providers list that excludes dyson are skipped. " +
                "AutoInclude openrules entries are already in the system prompt — prefer GetOpenRulesConfig / do not re-LoadSkill them. " +
                "loadIndexOnly is required: true = entry skill file only (SKILL.md if present, else first *.md; " +
                "single files are that file); false = concatenate all *.md under the directory (entry first). " +
                "Readonly — prefer this over ReadFile for known skills.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "name": {
                      "type": "string",
                      "description": "Skill id/stem (e.g. JDSL), .dyson/skills name, work-relative path, or openrules AgentOptional path/stem/URL."
                    },
                    "loadIndexOnly": {
                      "type": "boolean",
                      "description": "true = entry skill file only; false = full directory markdown concat."
                    }
                  },
                  "required": ["name", "loadIndexOnly"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ReadFile",
            Description =
                "Read workspace file contents by path. Prefer this over shell for reading files. " +
                "Each line is formatted as lineNumber|content (e.g. '42|    foo();'). " +
                "When copying into WriteFile old_text/new_text, use only the content after the first '|' — never include the line-number prefix. " +
                "Capped at 32KiB (~<20K tokens); larger slices error (IsError) with instruction to pass offset+limit or Grep first — file body is not returned. " +
                "offset is 1-based; negative = tail (e.g. -80 = last 80 lines). Per-line clip is 8KiB. Binary/image files error — use LoadBinary.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Workspace-relative or absolute file path." },
                    "offset": { "type": "integer", "description": "1-based start line; negative = tail from EOF (e.g. -80 = last 80 lines)." },
                    "limit": { "type": "integer", "description": "Max lines to return. Omit only for small files; over 32KiB the tool errors — use limit or Grep." }
                  },
                  "required": ["path"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CreateFile",
            Description =
                "Create a new file with content. By default, path is the requested workspace-relative or absolute destination and the call fails if it already exists unless overwrite is true. " +
                "For a generated temporary visualization asset, set isTempFile to true and pass path as a leaf file name with extension, such as chart.html, chart.css, or chart.js. " +
                "In temp mode, the harness requires a leaf name, sanitizes it, inserts a random suffix before the extension, writes the file under .dyson/temp/, and returns the exact generated workspace-relative path; overwrite must be omitted or false. " +
                "Use that returned path verbatim as a tempFile in RenderHtmlVisualization—never invent a .dyson/temp/ path. Put a dependent render call in a later harness stage so file creation finishes first. " +
                "Temporary content is limited to 512 KiB UTF-8, is ignored by git, and is not automatically deleted.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": {
                      "type": "string",
                      "description": "Normal destination path, or a leaf file name with extension when isTempFile is true."
                    },
                    "content": {
                      "type": "string",
                      "description": "Full file contents. Temporary files are limited to 512 KiB UTF-8."
                    },
                    "overwrite": {
                      "type": "boolean",
                      "description": "If true, replace an existing normal file. Must be omitted or false when isTempFile is true."
                    },
                    "isTempFile": {
                      "type": "boolean",
                      "description": "When true, treat path as a leaf name, add a random suffix, and create the file under .dyson/temp/. Default false."
                    }
                  },
                  "required": ["path", "content"],
                  "additionalProperties": false
                }
                """,
        };

        yield return CreateRenderHtmlVisualizationTool(uiTheme);

        yield return CreateGenerateImageTool();

        yield return new DysonMcpTool
        {
            Name = "WriteFile",
            Description =
                "Update a workspace file. Prefer targeted old_text/new_text (or edits[]) after ReadFile — not full-file rewrites. " +
                "Never include ReadFile line prefixes (e.g. '123|') in old_text/new_text; copy only the content after '|'. " +
                "The match must be unique unless replace_all is true. " +
                "Fuzzy matching tolerates whitespace, indentation, and EOL (CRLF/LF) differences when the match is unique. " +
                "Use content only for create-like full rewrites when targeted edits are impractical.",
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
            Name = "Grep",
            Description =
                "Search text file contents with a .NET regex (System.Text.RegularExpressions; not a literal substring, not ripgrep/PCRE/JavaScript). " +
                "Matches each line independently. Optional path and filename glob. " +
                "Text-only: never returns binary/image bytes. Skips .git/bin/obj/node_modules/.vs and similar. " +
                "Binary/image hits are path-only lines (binary\\t… / image\\t…) when the relative path matches; " +
                "use LoadBinary to inspect those files.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "pattern": { "type": "string", "description": ".NET regex (System.Text.RegularExpressions). Not a literal and not a glob — do not pass **/* here. Invalid patterns return error \"Invalid regex: …\"." },
                    "path": { "type": "string", "description": "Optional directory or file to search under (default .)." },
                    "glob": { "type": "string", "description": "Optional filename-only filter (* and ?; e.g. *.cs). Matched against the file name, not the path. ** and path globs like **/*.cs do not work — put the directory in path and the name pattern in glob." },
                    "caseInsensitive": { "type": "boolean", "description": "Case-insensitive search when true." },
                    "maxMatches": { "type": "integer", "description": "Optional cap on matches returned (default 100)." }
                  },
                  "required": ["pattern"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "LoadBinary",
            Description =
                "Load a binary or image file from the work directory into the next provider request. " +
                "Tool result Content is a short JSON ack (path, fileName, extension, mimeType, byteLength) — no base64. " +
                "Bytes are attached with the original filename+extension for Completions/Responses multimodal parts. " +
                "Images require FileStorage (presigned HTTPS URL); unconfigured returns file_storage_required. " +
                "Use after Grep returns binary\\t / image\\t path lines. Max size 5 MB.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": {
                      "type": "string",
                      "description": "Work-directory-relative path to the binary or image file."
                    }
                  },
                  "required": ["path"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ConvertImage",
            Description =
                "Convert or re-encode a work-directory image via Magick.NET and write the result to outputFile. " +
                "Supports SVG input and ICO output; desiredFormat may match the input (same-format re-encode / compress). " +
                "quality (1–100, default 85) is Magick Quality — primary knob for shrinking large JPEG/WebP. " +
                "overwrite defaults false (fail if output exists). Soft input ceiling 50 MB (not LoadBinary’s 5 MB). " +
                "Returns JSON ack only (inputFile, outputFile, desiredFormat, quality, byteLength, width, height, inputByteLength) — no BinaryAttachment; use LoadBinary on the result if vision is needed.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "inputFile": {
                      "type": "string",
                      "description": "Work-directory-relative path to the source image (must exist)."
                    },
                    "outputFile": {
                      "type": "string",
                      "description": "Work-directory-relative destination path for the converted image."
                    },
                    "desiredFormat": {
                      "type": "string",
                      "description": "Output format: png, jpeg/jpg, webp, gif, bmp, tiff/tif, or ico. May match input for re-encode/compress."
                    },
                    "quality": {
                      "type": "integer",
                      "description": "Magick Quality 1–100 (default 85). Lower values shrink JPEG/WebP size."
                    },
                    "overwrite": {
                      "type": "boolean",
                      "description": "If true, replace outputFile when it already exists. Default false."
                    }
                  },
                  "required": ["inputFile", "outputFile", "desiredFormat"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "ListDirectory",
            Description = "List entries in a directory. Prefer this over shell for directory listing.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Directory path to list." },
                    "recursive": { "type": "boolean", "description": "If true, list nested entries." }
                  },
                  "required": ["path"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CreateDirectory",
            Description = "Create a directory, including missing parent directories when createParents is true.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Directory path to create." },
                    "createParents": {
                      "type": "boolean",
                      "description": "If true, create intermediate parent directories as needed."
                    }
                  },
                  "required": ["path"]
                }
                """,
        };

        var shellExecute = CreateShellExecuteTool(availableShellNames);
        if (shellExecute is not null)
            yield return shellExecute;

        foreach (var longRunning in CreateLongRunningShellTools(availableShellNames, planMode: false))
            yield return longRunning;

        yield return new DysonMcpTool
        {
            Name = "FreeSearch",
            Description =
                "Web search across free engines (DuckDuckGo HTML first, then Bing RSS, Wikipedia; Brave when BRAVE_API_KEY / config is set). " +
                "Raw SERP JSON stays inside the tool; the parent receives a harness summary (skipped when already ≤~1500 tokens). " +
                "Prefer this over inventing URLs. Not for local codebase search (use Grep).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Search query." },
                    "count": { "type": "integer", "description": "Max results (1-20, default 10)." },
                    "engines": {
                      "type": "array",
                      "items": { "type": "string", "enum": ["duckduckgo", "bing", "wikipedia", "brave"] },
                      "description": "Optional engine allowlist. Default: duckduckgo+bing+wikipedia (+brave if keyed)."
                    },
                    "summarizePrompt": {
                      "type": "string",
                      "description": "Optional focus for the harness summarizer (e.g. what facts to keep). Raw payloads stay inside the tool; parent receives the summary."
                    }
                  },
                  "required": ["query"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "FreeSearchAdvanced",
            Description =
                "Advanced web search with waterfall phases, domain filters, min confidence, and optional Jina enrichment. " +
                "Raw results stay inside the tool; parent gets a harness summary (skipped when already ≤~1500 tokens). " +
                "Prefer FreeSearch for simple queries.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Search query." },
                    "count": { "type": "integer", "description": "Max results (1-20, default 5)." },
                    "minConfidence": { "type": "integer", "description": "Only return results with confidence >= N (1-3)." },
                    "includeDomains": { "type": "array", "items": { "type": "string" }, "description": "Only keep these domains." },
                    "excludeDomains": { "type": "array", "items": { "type": "string" }, "description": "Drop these domains." },
                    "waterfall": { "type": "boolean", "description": "Enable progressive engine phases (default true)." },
                    "waterfallMinResults": { "type": "integer", "description": "Min results for early waterfall stop." },
                    "waterfallMinConfidence": { "type": "number", "description": "Min avg confidence (0-1) for early stop." },
                    "enrich": { "type": "boolean", "description": "Enrich low-confidence snippets via Jina Reader." },
                    "enrichMax": { "type": "integer", "description": "Max results to enrich." },
                    "summarizePrompt": {
                      "type": "string",
                      "description": "Optional focus for the harness summarizer (e.g. what facts to keep). Raw payloads stay inside the tool; parent receives the summary."
                    }
                  },
                  "required": ["query"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SearchWithSynthesis",
            Description =
                "Waterfall search plus a string prompt_hint for the agent to synthesize an answer (no external LLM call for synthesis). " +
                "Raw results stay inside the tool; parent gets a harness summary (skipped when already ≤~1500 tokens).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Search query." },
                    "count": { "type": "integer", "description": "Max results (1-20, default 10)." },
                    "minConfidence": { "type": "integer", "description": "Only return results with confidence >= N (1-3)." },
                    "summarizePrompt": {
                      "type": "string",
                      "description": "Optional focus for the harness summarizer (e.g. what facts to keep). Raw payloads stay inside the tool; parent receives the summary."
                    }
                  },
                  "required": ["query"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WebFetch",
            Description =
                "Default tool for fetching public page content. Summarizes with the harness summarizer by default " +
                "(HTML never enters the parent transcript). Use fullHtml only when raw markup is required. SSRF-guarded.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "Public http(s) URL to fetch." },
                    "fullHtml": {
                      "type": "boolean",
                      "description": "When true, return the fetched HTML body to the parent (capped by maxBytes / large default). When false/omitted, summarize and return summary only. Do not set true unless raw HTML is required for the task."
                    },
                    "summarizePrompt": {
                      "type": "string",
                      "description": "Extra instructions for the summarizer (ignored when fullHtml is true). Tell it what to extract (e.g. list Billboard Global 200 #1 song and artist with source URL). Improves over a generic summary."
                    },
                    "maxBytes": {
                      "type": "integer",
                      "description": "Body cap in bytes. Default 64000 when summarizing; default 2000000 when fullHtml is true. Explicit maxBytes always wins."
                    }
                  },
                  "required": ["url"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "FetchGithubReadme",
            Description =
                "Fetch README from a GitHub repository via raw.githubusercontent.com. Pass a github.com owner/repo URL. " +
                "Raw README stays inside the tool; parent receives a harness summary (skipped when already ≤~1500 tokens).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "GitHub repository URL (https://github.com/owner/repo)." },
                    "summarizePrompt": {
                      "type": "string",
                      "description": "Optional focus for the harness summarizer (e.g. what facts to keep). Raw payloads stay inside the tool; parent receives the summary."
                    }
                  },
                  "required": ["url"]
                }
                """,
        };
    }
}

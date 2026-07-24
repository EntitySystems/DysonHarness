using System.Text;

namespace DysonHarness;

/// <summary>
/// Per-session MCP tool catalog. Format for prompt injection; live execution later.
/// Custom agent authors who want MCP-over-shell parity should include that preference in their own prompts.
/// </summary>
public sealed class DysonMcpPipeline
{
    public DysonMcpAccessMode AccessMode { get; }
    public Dictionary<string, DysonMcpTool> Tools { get; } = new(StringComparer.Ordinal);
    public DysonMcpAutoReviewProxy? AutoReviewProxy { get; }

    private DysonMcpPipeline(DysonMcpAccessMode accessMode)
    {
        AccessMode = accessMode;
        AutoReviewProxy = accessMode == DysonMcpAccessMode.AutoReview
            ? new DysonMcpAutoReviewProxy(this)
            : null;
    }

    public static DysonMcpPipeline CreateDefault(
        DysonMcpAccessMode accessMode,
        IReadOnlyList<DysonShellType>? availableShellTypes = null)
    {
        availableShellTypes ??= DysonShell.AvailableForCurrentPlatform();
        var pipeline = new DysonMcpPipeline(accessMode);
        foreach (var tool in DefaultTools(availableShellTypes))
            pipeline.Tools[tool.Name] = tool;
        return pipeline;
    }

    /// <summary>
    /// Builds ShellExecute with a shell enum matching the session's available types.
    /// Returns null when no shells are available for the platform.
    /// </summary>
    public static DysonMcpTool? CreateShellExecuteTool(IReadOnlyList<DysonShellType> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0)
            return null;

        var names = available.Select(t => t.ToString()).ToArray();
        var listed = string.Join(", ", names);
        var enumJson = string.Join(", ", names.Select(n => $"\"{n}\""));

        return new DysonMcpTool
        {
            Name = "ShellExecute",
            Description =
                "Run a command in the session work directory. " +
                $"Available shells for this session: {listed}. " +
                "You must pass shell as one of these. Prefer dedicated MCP file tools over shell when they fit.",
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
                      "description": "Command line to execute in the chosen shell."
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

    private static IEnumerable<DysonMcpTool> DefaultTools(IReadOnlyList<DysonShellType> availableShellTypes)
    {
        yield return new DysonMcpTool
        {
            Name = "StartSubagent",
            Description =
                "Spawn a nested agent session (Drone or another mode) for delegated work. " +
                "The spawned agent receives a unique integer Id (≥ 1). " +
                "Completion surfaces via parent interrupts or WaitForSubagent. " +
                "Use when parallel or isolated work clearly helps; pass a clear task brief.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "agentMode": { "type": "string", "description": "Mode for the sub-agent (e.g. Drone)." },
                    "task": { "type": "string", "description": "Assigned task brief for the sub-agent." },
                    "context": { "type": "string", "description": "Optional extra context or constraints." }
                  },
                  "required": ["agentMode", "task"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WaitForSubagent",
            Description =
                "Block/wait until a subagent completes (or until timeout). " +
                "Parent multitasking uses the interrupt queue under the hood later.",
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
                      "description": "Optional max wait in milliseconds before returning."
                    }
                  },
                  "required": ["subagentId"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "InspectSubagentLog",
            Description = "Read recent log lines for a subagent by Id.",
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
            Description = "Request cooperative stop / cancel of a running subagent.",
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
            Name = "CompleteTask",
            Description =
                "Request completion review: the harness schedules a confirmation turn rather than ending immediately. " +
                "On that follow-up turn you must call ConfirmTaskComplete or ContinueWork. " +
                "After ConfirmTaskComplete, a ReportSummary turn follows.",
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
            Name = "ExpandThoughtProcess",
            Description =
                "Request a special reformulation turn before continuing heavy work. " +
                "Use when context is noisy or the plan is unclear.",
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
            Name = "ReadFile",
            Description = "Read workspace file contents by path. Prefer this over shell for reading files.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Workspace-relative or absolute file path." },
                    "offset": { "type": "integer", "description": "Optional 1-based start line." },
                    "limit": { "type": "integer", "description": "Optional max number of lines to return." }
                  },
                  "required": ["path"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "CreateFile",
            Description = "Create a new file with content. Fails if the path already exists unless overwrite is true.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Path of the file to create." },
                    "content": { "type": "string", "description": "Full file contents." },
                    "overwrite": { "type": "boolean", "description": "If true, replace an existing file." }
                  },
                  "required": ["path", "content"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WriteFile",
            Description =
                "Update an existing file using diff-oriented edits. Prefer patch/hunk-style or targeted span updates " +
                "(old_text/new_text or edits[]) over full-file rewrites when possible.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Path of the file to update." },
                    "old_text": { "type": "string", "description": "Exact text span to replace (single edit)." },
                    "new_text": { "type": "string", "description": "Replacement text for old_text." },
                    "edits": {
                      "type": "array",
                      "description": "Ordered list of targeted replacements when multiple hunks are needed.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "old_text": { "type": "string" },
                          "new_text": { "type": "string" }
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
            Description = "Search file contents by regex/pattern with optional path and glob filters.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "pattern": { "type": "string", "description": "Regex or literal search pattern." },
                    "path": { "type": "string", "description": "Optional directory or file to search under." },
                    "glob": { "type": "string", "description": "Optional glob filter (e.g. *.cs)." },
                    "caseInsensitive": { "type": "boolean", "description": "Case-insensitive search when true." },
                    "maxMatches": { "type": "integer", "description": "Optional cap on matches returned." }
                  },
                  "required": ["pattern"]
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

        var shellExecute = CreateShellExecuteTool(availableShellTypes);
        if (shellExecute is not null)
            yield return shellExecute;

        yield return new DysonMcpTool
        {
            Name = "FreeSearch",
            Description =
                "Web search across free engines (Bing + Wikipedia; Brave when BRAVE_API_KEY / config is set). " +
                "Returns JSON results with title, url, snippet, and confidence 1–3. " +
                "Prefer this over inventing URLs. Not for local codebase search (use Grep).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Search query." },
                    "count": { "type": "integer", "description": "Max results (1-20, default 10)." },
                    "engines": {
                      "type": "array",
                      "items": { "type": "string", "enum": ["bing", "wikipedia", "brave"] },
                      "description": "Optional engine allowlist. Default: bing+wikipedia (+brave if keyed)."
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
                    "enrichMax": { "type": "integer", "description": "Max results to enrich." }
                  },
                  "required": ["query"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "SearchWithSynthesis",
            Description =
                "Waterfall search plus a string prompt_hint for the agent to synthesize an answer (no external LLM call).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Search query." },
                    "count": { "type": "integer", "description": "Max results (1-20, default 10)." },
                    "minConfidence": { "type": "integer", "description": "Only return results with confidence >= N (1-3)." }
                  },
                  "required": ["query"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "FreeExtract",
            Description =
                "Extract page content as markdown via Jina Reader (r.jina.ai/{url}). SSRF-guarded.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "Public http(s) URL to extract." },
                    "maxLength": { "type": "integer", "description": "Max characters to return (default 5000)." }
                  },
                  "required": ["url"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WebFetch",
            Description =
                "HTTP GET a public URL and return raw HTML plus statusCode, contentType, finalUrl. SSRF-guarded. " +
                "Prefer FreeExtract when you need readable markdown.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "Public http(s) URL to fetch." },
                    "maxBytes": { "type": "integer", "description": "Optional response body cap in bytes." }
                  },
                  "required": ["url"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "FetchGithubReadme",
            Description =
                "Fetch README from a GitHub repository via raw.githubusercontent.com. Pass a github.com owner/repo URL.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "GitHub repository URL (https://github.com/owner/repo)." }
                  },
                  "required": ["url"]
                }
                """,
        };
    }
}

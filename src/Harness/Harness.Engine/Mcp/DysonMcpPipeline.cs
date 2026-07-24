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
                "Spawn a nested agent session for delegated work (non-blocking). " +
                "Returns immediately with subagentId / persistenceId; the child runs in the background. " +
                "When the child calls SubmitSubagentReport, the parent is notified and the host queues a turn — " +
                "do not WaitForSubagent unless that child’s result is a blocker. " +
                "Plan is banned as a subagent mode. Explore cannot spawn. Drone may spawn Explore only (not another Drone).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "agentMode": { "type": "string", "description": "Mode for the sub-agent (e.g. Explore, Drone). Not Plan." },
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
                "Block until this subagent finishes (completed / failed / stopped) or timeoutMs. " +
                "Default timeout is 300000 ms (5 minutes) when timeoutMs is omitted. " +
                "Use only when its result is required before the parent can proceed (prerequisite/blocker). " +
                "If the parent can do other work, do not Wait — spawn and continue; the harness queues a turn when SubmitSubagentReport arrives.",
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
                "Notifies the parent with the summary so the host can queue a parent turn. " +
                "Do not use from a root Work session unless debugging.",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "summary": {
                      "type": "string",
                      "description": "Crisp handoff for the parent (findings, outcome, blockers)."
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
            Name = "FreeExtract",
            Description =
                "Extract page content as markdown via Jina Reader (r.jina.ai/{url}). SSRF-guarded. " +
                "Raw extract stays inside the tool; parent receives a harness summary (skipped when already ≤~1500 tokens).",
            InputSchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "Public http(s) URL to extract." },
                    "maxLength": { "type": "integer", "description": "Max characters to return (default 5000)." },
                    "summarizePrompt": {
                      "type": "string",
                      "description": "Optional focus for the harness summarizer (e.g. what facts to keep). Raw payloads stay inside the tool; parent receives the summary."
                    }
                  },
                  "required": ["url"]
                }
                """,
        };

        yield return new DysonMcpTool
        {
            Name = "WebFetch",
            Description =
                "Fetch a URL. Default: load the page, summarize with the harness summarizer, return only the summary " +
                "(HTML never enters the parent transcript). Use fullHtml only when the agent truly needs raw markup. " +
                "Prefer FreeExtract for readable article text; use WebFetch when HTML structure or a directed summary is required. SSRF-guarded.",
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

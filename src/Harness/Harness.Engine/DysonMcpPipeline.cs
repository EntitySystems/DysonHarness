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

    public static DysonMcpPipeline CreateDefault(DysonMcpAccessMode accessMode)
    {
        var pipeline = new DysonMcpPipeline(accessMode);
        foreach (var tool in DefaultTools())
            pipeline.Tools[tool.Name] = tool;
        return pipeline;
    }

    /// <summary>Formats the tools dictionary into a prompt-injectable catalog string.</summary>
    public string FormatToolsForPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Available MCP tools");

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

    private static IEnumerable<DysonMcpTool> DefaultTools()
    {
        yield return new DysonMcpTool
        {
            Name = "StartSubagent",
            Description =
                "Spawn a nested agent session (Drone or another mode) for delegated work. " +
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
            Name = "CompleteTask",
            Description =
                "Signal that the current task is finished and hand off a concise result summary to the parent or user.",
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
    }
}

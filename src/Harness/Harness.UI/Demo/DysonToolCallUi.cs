using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Pure parse/summary helpers for tool-call row UI variants.</summary>
public static class DysonToolCallUi
{
    public const int SummaryMaxLength = 72;
    public const int PreviewMaxLength = 160;

    public sealed class CollapsedSummary
    {
        public string? Text { get; init; }
        public int? LinesAdded { get; init; }
        public int? LinesRemoved { get; init; }
        public bool HasLineDelta => LinesAdded is not null || LinesRemoved is not null;
    }

    public sealed class ShellExecuteParsed
    {
        public string? Shell { get; init; }
        public string? Command { get; init; }
        public string? WorkingDirectory { get; init; }
        public string? PlanWarning { get; init; }
        public int? ExitCode { get; init; }
        public bool TimedOut { get; init; }
        public string? Stdout { get; init; }
        public string? Stderr { get; init; }
    }

    public sealed class WriteFileParsed
    {
        public string? Path { get; init; }
        public int LinesAdded { get; init; }
        public int LinesRemoved { get; init; }
        public int EditCount { get; init; }
        public bool IsFullRewrite { get; init; }
    }

    public static CollapsedSummary GetCollapsedSummary(
        string toolName,
        string? argumentsJson,
        string? resultContent,
        bool hasResult)
    {
        return toolName switch
        {
            "WriteFile" => SummarizeWriteFile(argumentsJson),
            "CreateFile" => SummarizeCreateFile(argumentsJson),
            "ReadFile" => SummarizeReadFile(argumentsJson),
            "Grep" => SummarizeGrep(argumentsJson, resultContent, hasResult),
            "ListDirectory" => SummarizeListDirectory(argumentsJson, resultContent, hasResult),
            "CreateDirectory" => TextSummary(Truncate(GetString(argumentsJson, "path"), SummaryMaxLength)),
            "ShellExecute" => SummarizeShellExecute(argumentsJson, resultContent, hasResult),
            "StartLongRunningShell" => SummarizeStartLongRunningShell(argumentsJson, resultContent, hasResult),
            "ListLongRunningShells" => SummarizeListLongRunningShells(resultContent, hasResult),
            "ReadLongRunningShellTail" => SummarizeReadLongRunningShellTail(argumentsJson, resultContent, hasResult),
            "AbortLongRunningShell" => SummarizeLongRunningShellId(argumentsJson, "abort"),
            "RequestLongRunningShellCancellation" => SummarizeLongRunningShellId(argumentsJson, "cancel"),
            "LongRunningShellInteract" => SummarizeLongRunningShellInteract(argumentsJson),
            "SubscribeToLongRunningShellCompletion" => SummarizeLongRunningShellId(argumentsJson, "subscribe"),
            "RenameSession" => TextSummary(Quote(Truncate(GetString(argumentsJson, "title"), SummaryMaxLength - 2))),
            "GetDateTime" => SummarizeGetDateTime(argumentsJson, resultContent, hasResult),
            "SubmitPlan" => TextSummary(Truncate(GetString(argumentsJson, "title"), SummaryMaxLength)),
            "ListTodos" => SummarizeListTodos(resultContent, hasResult),
            "CreateTodo" => TextSummary(Truncate(GetString(argumentsJson, "displayName"), SummaryMaxLength)),
            "UpdateTodo" => SummarizeUpdateTodo(argumentsJson),
            "DeleteTodo" => TextSummary(Truncate(
                GetString(argumentsJson, "taskCode") ?? GetString(argumentsJson, "displayName"),
                SummaryMaxLength)),
            "StartSubagent" => SummarizeStartSubagent(argumentsJson),
            "ListSubagents" => SummarizeListSubagents(resultContent, hasResult),
            "WaitForSubagent" => SummarizeWaitForSubagent(argumentsJson, resultContent, hasResult),
            "InspectSubagentLog" => SummarizeInspectSubagentLog(argumentsJson),
            "StopSubagent" => TextSummary($"#{GetInt(argumentsJson, "subagentId")}"),
            "SubmitSubagentReport" => SummarizeSubmitReport(argumentsJson),
            "AskQuestion" => SummarizeAskQuestion(argumentsJson, viaParent: false),
            "AskQuestionFromParent" => SummarizeAskQuestion(argumentsJson, viaParent: true),
            "PromptUserDialog" => SummarizePromptUserDialog(argumentsJson, viaParent: false),
            "PromptUserDialogFromParent" => SummarizePromptUserDialog(argumentsJson, viaParent: true),
            "TriggerParentEvent" => SummarizeTriggerParent(argumentsJson),
            "RespondToSubagentEvent" => SummarizeRespondToSubagent(argumentsJson),
            "TriggerSubagentEvent" => SummarizeTriggerSubagent(argumentsJson),
            "CompleteTask" => TextSummary(FirstLine(GetString(argumentsJson, "summary"), SummaryMaxLength)),
            "ConfirmTaskComplete" => SummarizeConfirm(argumentsJson),
            "ContinueWork" => TextSummary(Truncate(
                GetString(argumentsJson, "reason") ?? GetString(argumentsJson, "remainingWork") ?? "continue",
                SummaryMaxLength)),
            "ResumeCurrentTask" => TextSummary(Truncate(
                GetString(argumentsJson, "rationale")
                ?? GetString(argumentsJson, "continuationInstructions")
                ?? "resume",
                SummaryMaxLength)),
            "WaitForSeconds" => TextSummary(SummarizeWaitForSeconds(argumentsJson)),
            "JsonDynamicStructuredLanguageToolchain" => SummarizeJsonDynamicToolchain(resultContent, hasResult),
            "ExpandThoughtProcess" => TextSummary(Truncate(
                GetString(argumentsJson, "focus") ?? "reformulate",
                SummaryMaxLength)),
            "StartNewTurn" => TextSummary(Truncate(
                GetString(argumentsJson, "promptInstructions") ?? "new turn",
                SummaryMaxLength)),
            "DropTurnContext" => TextSummary(SummarizeDropTurnContext(argumentsJson)),
            "RestoreTurnContext" => TextSummary(SummarizeRestoreTurnContext(argumentsJson)),
            "FreeSearch" or "FreeSearchAdvanced" or "SearchWithSynthesis"
                => TextSummary(Truncate(GetString(argumentsJson, "query"), SummaryMaxLength)),
            "FreeExtract" => TextSummary(Truncate(UrlHost(GetString(argumentsJson, "url")), SummaryMaxLength)),
            "WebFetch" => SummarizeWebFetch(argumentsJson),
            "FetchGithubReadme" => TextSummary(Truncate(GithubOwnerRepo(GetString(argumentsJson, "url")), SummaryMaxLength)),
            "BrowserTakeScreenshot" => SummarizeScreenshot(resultContent, hasResult),
            _ => TextSummary(Truncate(CompactJson(argumentsJson), SummaryMaxLength)),
        };
    }

    public sealed class ScreenshotAckParsed
    {
        public string? MimeType { get; init; }
        public int? ByteLength { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
    }

    public static ScreenshotAckParsed? TryParseScreenshotAck(string? resultContent)
    {
        if (!TryParseObject(resultContent, out var root))
            return null;

        int? byteLength = null;
        if (root.TryGetProperty("byteLength", out var bl) && bl.ValueKind == JsonValueKind.Number
            && bl.TryGetInt32(out var n))
        {
            byteLength = n;
        }

        int? width = null;
        if (root.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number
            && w.TryGetInt32(out var wi))
        {
            width = wi;
        }

        int? height = null;
        if (root.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number
            && h.TryGetInt32(out var hi))
        {
            height = hi;
        }

        return new ScreenshotAckParsed
        {
            MimeType = GetPropString(root, "mimeType"),
            ByteLength = byteLength,
            Width = width,
            Height = height,
        };
    }

    public static string FormatByteSize(int bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024)
            return $"{kb:0.#} KB";
        return $"{kb / 1024.0:0.#} MB";
    }

    private static CollapsedSummary SummarizeScreenshot(string? resultContent, bool hasResult)
    {
        if (!hasResult)
            return TextSummary("screenshot");

        var parsed = TryParseScreenshotAck(resultContent);
        if (parsed is null)
            return TextSummary("screenshot");

        var parts = new List<string> { "screenshot" };
        if (parsed.Width is int w && parsed.Height is int h)
            parts.Add($"{w}x{h}");
        if (parsed.ByteLength is int bytes)
            parts.Add(FormatByteSize(bytes));
        return TextSummary(Truncate(string.Join(" · ", parts), SummaryMaxLength));
    }

    public static WriteFileParsed? TryParseWriteFile(string? argumentsJson)
    {
        if (!TryParseObject(argumentsJson, out var root))
            return null;

        var path = GetPropString(root, "path");
        var added = 0;
        var removed = 0;
        var edits = 0;
        var fullRewrite = false;

        if (root.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.String
            && !root.TryGetProperty("old_text", out _)
            && !root.TryGetProperty("edits", out _))
        {
            fullRewrite = true;
            added = CountLines(contentProp.GetString());
            edits = 1;
        }
        else
        {
            if (root.TryGetProperty("old_text", out var oldProp)
                && root.TryGetProperty("new_text", out var newProp))
            {
                removed += CountLines(oldProp.GetString());
                added += CountLines(newProp.GetString());
                edits++;
            }

            if (root.TryGetProperty("edits", out var editsArr)
                && editsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var edit in editsArr.EnumerateArray())
                {
                    if (!edit.TryGetProperty("old_text", out var o) || !edit.TryGetProperty("new_text", out var n))
                        continue;
                    removed += CountLines(o.GetString());
                    added += CountLines(n.GetString());
                    edits++;
                }
            }
        }

        return new WriteFileParsed
        {
            Path = path,
            LinesAdded = added,
            LinesRemoved = removed,
            EditCount = edits,
            IsFullRewrite = fullRewrite,
        };
    }

    public static ShellExecuteParsed ParseShellExecute(string? argumentsJson, string? resultContent)
    {
        TryParseObject(argumentsJson, out var root);
        var shell = root.ValueKind == JsonValueKind.Object ? GetPropString(root, "shell") : null;
        var command = root.ValueKind == JsonValueKind.Object ? GetPropString(root, "command") : null;
        var cwd = root.ValueKind == JsonValueKind.Object ? GetPropString(root, "workingDirectory") : null;

        string? planWarning = null;
        string? stdout = null;
        string? stderr = null;
        int? exitCode = null;
        var timedOut = false;

        if (!string.IsNullOrEmpty(resultContent))
        {
            var text = resultContent.Replace("\r\n", "\n", StringComparison.Ordinal);
            var warning = DysonMcpPipeline.PlanShellExecuteWarning;
            if (text.StartsWith(warning, StringComparison.Ordinal))
            {
                planWarning = warning;
                text = text[warning.Length..].TrimStart('\n', '\r', ' ');
            }

            var exitMatch = Regex.Match(text, @"^exitCode=(-?\d+)(?:\s+timedOut=true)?", RegexOptions.Multiline);
            if (exitMatch.Success)
            {
                if (int.TryParse(exitMatch.Groups[1].Value, out var code))
                    exitCode = code;
                timedOut = exitMatch.Value.Contains("timedOut=true", StringComparison.Ordinal);
            }

            stdout = ExtractSection(text, "--- stdout ---");
            stderr = ExtractSection(text, "--- stderr ---");
        }

        return new ShellExecuteParsed
        {
            Shell = shell,
            Command = command,
            WorkingDirectory = cwd,
            PlanWarning = planWarning,
            ExitCode = exitCode,
            TimedOut = timedOut,
            Stdout = stdout,
            Stderr = stderr,
        };
    }

    public static string? GetString(string? argumentsJson, string propertyName)
    {
        if (!TryParseObject(argumentsJson, out var root))
            return null;
        return GetPropString(root, propertyName);
    }

    public static int? GetInt(string? argumentsJson, string propertyName)
    {
        if (!TryParseObject(argumentsJson, out var root))
            return null;
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
            return n;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return null;
    }

    public static bool GetBool(string? argumentsJson, string propertyName, bool defaultValue = false)
    {
        if (!TryParseObject(argumentsJson, out var root))
            return defaultValue;
        if (!root.TryGetProperty(propertyName, out var prop))
            return defaultValue;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    public static IReadOnlyList<string> GetStringArray(string? argumentsJson, string propertyName)
    {
        if (!TryParseObject(argumentsJson, out var root))
            return [];
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? "");
        }

        return list;
    }

    public static string? GetJsonProperty(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || !TryParseObject(json, out var root))
            return null;
        return GetPropString(root, propertyName);
    }

    public static int CountJsonArrayItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var n = 1;
        foreach (var c in text)
        {
            if (c == '\n')
                n++;
        }

        return n;
    }

    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var oneLine = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        if (oneLine.Length <= max)
            return oneLine;
        return max <= 1 ? "…" : oneLine[..(max - 1)] + "…";
    }

    public static string FirstLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var line = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var nl = line.IndexOf('\n');
        if (nl >= 0)
            line = line[..nl];
        return Truncate(line.Trim(), max);
    }

    public static string Basename(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    public static string? UrlHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Truncate(url, SummaryMaxLength);
        return string.IsNullOrEmpty(uri.Host) ? Truncate(url, SummaryMaxLength) : uri.Host;
    }

    public static string? GithubOwnerRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Truncate(url, SummaryMaxLength);
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{parts[0]}/{parts[1]}";
        return UrlHost(url);
    }

    public static int CountGrepMatches(string? resultContent)
    {
        if (string.IsNullOrWhiteSpace(resultContent))
            return 0;
        if (resultContent.StartsWith("No matches.", StringComparison.Ordinal))
            return 0;
        var count = 0;
        using var reader = new StringReader(resultContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('…') || string.IsNullOrWhiteSpace(line))
                continue;
            // path:line:content
            var first = line.IndexOf(':');
            if (first <= 0)
                continue;
            var second = line.IndexOf(':', first + 1);
            if (second <= first)
                continue;
            count++;
        }

        return count;
    }

    public static int CountListEntries(string? resultContent)
    {
        if (string.IsNullOrWhiteSpace(resultContent) || resultContent == "(empty)")
            return 0;
        var n = 0;
        using var reader = new StringReader(resultContent);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                n++;
        }

        return n;
    }

    public static (string? Path, int? Line, string? Text) ParseGrepMatchLine(string line)
    {
        var first = line.IndexOf(':');
        if (first <= 0)
            return (null, null, line);
        var second = line.IndexOf(':', first + 1);
        if (second <= first)
            return (null, null, line);
        var path = line[..first];
        var linePart = line[(first + 1)..second];
        var text = line[(second + 1)..];
        return int.TryParse(linePart, out var ln) ? (path, ln, text) : (null, null, line);
    }

    public static (string Kind, string Path)? ParseListEntryLine(string line)
    {
        var tab = line.IndexOf('\t');
        if (tab <= 0)
            return null;
        return (line[..tab], line[(tab + 1)..]);
    }

    private static CollapsedSummary TextSummary(string? text) => new() { Text = string.IsNullOrEmpty(text) ? null : text };

    private static CollapsedSummary SummarizeWriteFile(string? argumentsJson)
    {
        var parsed = TryParseWriteFile(argumentsJson);
        if (parsed is null)
            return TextSummary(null);
        var name = Basename(parsed.Path);
        return new CollapsedSummary
        {
            Text = string.IsNullOrEmpty(name) ? null : Truncate(name, SummaryMaxLength),
            LinesAdded = parsed.LinesAdded,
            LinesRemoved = parsed.IsFullRewrite ? null : parsed.LinesRemoved,
        };
    }

    private static CollapsedSummary SummarizeCreateFile(string? argumentsJson)
    {
        var path = GetString(argumentsJson, "path");
        var content = GetString(argumentsJson, "content") ?? "";
        var name = Basename(path);
        var lines = CountLines(content);
        var summary = string.IsNullOrEmpty(name)
            ? $"{content.Length} chars"
            : $"{name} · {lines} lines · {content.Length} chars";
        return TextSummary(Truncate(summary, SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeReadFile(string? argumentsJson)
    {
        var path = Basename(GetString(argumentsJson, "path"));
        var offset = GetInt(argumentsJson, "offset");
        var limit = GetInt(argumentsJson, "limit");
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(path))
            sb.Append(path);
        if (offset is int o)
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append('L').Append(o);
            if (limit is int lim)
                sb.Append('…').Append(o + lim - 1);
        }
        else if (limit is int limOnly)
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append("limit ").Append(limOnly);
        }

        return TextSummary(Truncate(sb.ToString(), SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeGrep(string? argumentsJson, string? resultContent, bool hasResult)
    {
        var pattern = Truncate(GetString(argumentsJson, "pattern"), 40);
        if (!hasResult)
            return TextSummary(pattern);
        if (string.IsNullOrWhiteSpace(resultContent) || resultContent.StartsWith("No matches.", StringComparison.Ordinal))
            return TextSummary(string.IsNullOrEmpty(pattern) ? "no matches" : $"{pattern} · no matches");
        var n = CountGrepMatches(resultContent);
        return TextSummary($"{pattern} · {n} match{(n == 1 ? "" : "es")}");
    }

    private static CollapsedSummary SummarizeListDirectory(string? argumentsJson, string? resultContent, bool hasResult)
    {
        var path = Truncate(GetString(argumentsJson, "path"), 40);
        var recursive = GetBool(argumentsJson, "recursive");
        if (!hasResult)
        {
            var pending = recursive ? $"{path} · recursive" : path;
            return TextSummary(Truncate(pending, SummaryMaxLength));
        }

        var n = CountListEntries(resultContent);
        var text = string.IsNullOrEmpty(path) ? $"{n} entries" : $"{path} · {n}";
        if (recursive)
            text += " · recursive";
        return TextSummary(Truncate(text, SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeShellExecute(string? argumentsJson, string? resultContent, bool hasResult)
    {
        var parsed = ParseShellExecute(argumentsJson, hasResult ? resultContent : null);
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(parsed.Shell))
            sb.Append(parsed.Shell);
        var cmd = FirstLine(parsed.Command, 40);
        if (!string.IsNullOrEmpty(cmd))
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(cmd);
        }

        if (hasResult && parsed.ExitCode is int code)
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append("exit ").Append(code);
            if (parsed.TimedOut)
                sb.Append(" timeout");
        }

        return TextSummary(Truncate(sb.ToString(), SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeStartLongRunningShell(
        string? argumentsJson,
        string? resultContent,
        bool hasResult)
    {
        var shell = GetString(argumentsJson, "shell");
        var cmd = FirstLine(GetString(argumentsJson, "command"), 36);
        var id = hasResult ? TryParseLongRunningShellId(resultContent) : null;
        var sb = new StringBuilder();
        if (id is int n)
            sb.Append('#').Append(n);
        if (!string.IsNullOrEmpty(shell))
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(shell);
        }

        if (!string.IsNullOrEmpty(cmd))
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(cmd);
        }

        return TextSummary(Truncate(sb.ToString(), SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeListLongRunningShells(string? resultContent, bool hasResult)
    {
        if (!hasResult || string.IsNullOrWhiteSpace(resultContent))
            return TextSummary("list");

        var trimmed = resultContent.Trim();
        if (trimmed == "[]")
            return TextSummary("0 shells");

        var count = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '{')
                count++;
        }

        return TextSummary(count > 0 ? $"{count} shells" : "list");
    }

    private static CollapsedSummary SummarizeReadLongRunningShellTail(
        string? argumentsJson,
        string? resultContent,
        bool hasResult)
    {
        var id = GetInt(argumentsJson, "longRunningShellId");
        var status = hasResult ? TryParseStatusToken(resultContent) : null;
        var sb = new StringBuilder();
        if (id is int n)
            sb.Append('#').Append(n);
        if (!string.IsNullOrEmpty(status))
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(status);
        }
        else if (sb.Length == 0)
            sb.Append("tail");

        return TextSummary(Truncate(sb.ToString(), SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeLongRunningShellId(string? argumentsJson, string verb)
    {
        var id = GetInt(argumentsJson, "longRunningShellId");
        return TextSummary(id is int n ? $"#{n} · {verb}" : verb);
    }

    private static CollapsedSummary SummarizeLongRunningShellInteract(string? argumentsJson)
    {
        var id = GetInt(argumentsJson, "longRunningShellId");
        var input = FirstLine(GetString(argumentsJson, "input"), 28);
        var sb = new StringBuilder();
        if (id is int n)
            sb.Append('#').Append(n);
        if (!string.IsNullOrEmpty(input))
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(input);
        }

        return TextSummary(Truncate(sb.Length == 0 ? "interact" : sb.ToString(), SummaryMaxLength));
    }

    private static int? TryParseLongRunningShellId(string? resultContent)
    {
        if (string.IsNullOrEmpty(resultContent))
            return null;
        foreach (var line in resultContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            const string prefix = "longRunningShellId=";
            if (line.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(prefix.Length).Trim(), out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static string? TryParseStatusToken(string? resultContent)
    {
        if (string.IsNullOrEmpty(resultContent))
            return null;
        foreach (var part in resultContent.Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "status=";
            if (part.StartsWith(prefix, StringComparison.Ordinal))
                return part[prefix.Length..];
        }

        return null;
    }

    private static CollapsedSummary SummarizeGetDateTime(string? argumentsJson, string? resultContent, bool hasResult)
    {
        var tz = GetString(argumentsJson, "timezone") ?? "utc";
        if (hasResult && !string.IsNullOrEmpty(resultContent))
        {
            foreach (var line in resultContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                if (line.StartsWith("display: ", StringComparison.Ordinal))
                {
                    var display = line["display: ".Length..].Trim();
                    return TextSummary(Truncate($"{display} · {tz}", SummaryMaxLength));
                }
            }
        }

        return TextSummary(tz);
    }

    private static CollapsedSummary SummarizeListTodos(string? resultContent, bool hasResult)
    {
        if (!hasResult)
            return TextSummary("todos");
        var n = CountJsonArrayItems(resultContent);
        return TextSummary($"{n} todo{(n == 1 ? "" : "s")}");
    }

    private static CollapsedSummary SummarizeUpdateTodo(string? argumentsJson)
    {
        var code = GetString(argumentsJson, "taskCode");
        var status = GetString(argumentsJson, "status");
        var name = GetString(argumentsJson, "displayName");
        var label = !string.IsNullOrEmpty(name) ? name : code;
        if (!string.IsNullOrEmpty(status))
            label = string.IsNullOrEmpty(label) ? status : $"{label} · {status}";
        return TextSummary(Truncate(label, SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeStartSubagent(string? argumentsJson)
    {
        var mode = GetString(argumentsJson, "agentMode");
        var task = FirstLine(GetString(argumentsJson, "task"), 36);
        var model = GetString(argumentsJson, "modelSlug");
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(mode))
            sb.Append(mode);
        if (!string.IsNullOrEmpty(task))
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(task);
        }

        if (!string.IsNullOrEmpty(model))
        {
            if (sb.Length > 0)
                sb.Append(" · ");
            sb.Append(model);
        }

        return TextSummary(Truncate(sb.ToString(), SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeListSubagents(string? resultContent, bool hasResult)
    {
        if (!hasResult)
            return TextSummary("subagents");
        var n = CountJsonArrayItems(resultContent);
        return TextSummary($"{n} child{(n == 1 ? "" : "ren")}");
    }

    private static CollapsedSummary SummarizeWaitForSubagent(string? argumentsJson, string? resultContent, bool hasResult)
    {
        var id = GetInt(argumentsJson, "subagentId");
        var label = id is int i ? $"#{i}" : "wait";
        if (!hasResult)
            return TextSummary($"{label} · waiting");
        return TextSummary(Truncate($"{label} · completed", SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeInspectSubagentLog(string? argumentsJson)
    {
        var id = GetInt(argumentsJson, "subagentId");
        var max = GetInt(argumentsJson, "maxLines");
        var text = id is int i ? $"#{i}" : "log";
        if (max is int m)
            text += $" · {m} lines";
        return TextSummary(text);
    }

    private static CollapsedSummary SummarizeSubmitReport(string? argumentsJson)
    {
        var failed = string.Equals(GetString(argumentsJson, "status"), "failed", StringComparison.OrdinalIgnoreCase);
        return TextSummary(failed ? "report failed" : "report submitted");
    }

    private static CollapsedSummary SummarizeAskQuestion(string? argumentsJson, bool viaParent)
    {
        var n = 0;
        if (TryParseObject(argumentsJson, out var root)
            && root.TryGetProperty("questions", out var q)
            && q.ValueKind == JsonValueKind.Array)
        {
            n = q.GetArrayLength();
        }

        var text = $"{n} question{(n == 1 ? "" : "s")}";
        if (viaParent)
            text += " · via parent";
        return TextSummary(text);
    }

    private static CollapsedSummary SummarizePromptUserDialog(string? argumentsJson, bool viaParent)
    {
        var title = Truncate(GetString(argumentsJson, "title") ?? "dialog", 32);
        var n = 0;
        if (TryParseObject(argumentsJson, out var root)
            && root.TryGetProperty("actions", out var actions)
            && actions.ValueKind == JsonValueKind.Array)
        {
            n = actions.GetArrayLength();
        }

        var text = $"{title} · {n} action{(n == 1 ? "" : "s")}";
        if (viaParent)
            text += " · via parent";
        return TextSummary(Truncate(text, SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeTriggerParent(string? argumentsJson)
    {
        var kind = GetString(argumentsJson, "kind") ?? "event";
        var payload = Truncate(GetString(argumentsJson, "payload"), 40);
        return TextSummary(Truncate(string.IsNullOrEmpty(payload) ? kind : $"{kind} · {payload}", SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeRespondToSubagent(string? argumentsJson)
    {
        var id = GetInt(argumentsJson, "subagentId");
        var eventId = Truncate(GetString(argumentsJson, "eventId"), 12);
        var text = id is int i ? $"#{i}" : "reply";
        if (!string.IsNullOrEmpty(eventId))
            text += $" · {eventId}";
        return TextSummary(text);
    }

    private static CollapsedSummary SummarizeTriggerSubagent(string? argumentsJson)
    {
        var id = GetInt(argumentsJson, "subagentId");
        var interrupt = GetBool(argumentsJson, "interruptSubagent");
        var payload = Truncate(GetString(argumentsJson, "payload"), 36);
        var text = id is int i ? $"#{i}" : "event";
        text += interrupt ? " · interrupt" : " · queue";
        if (!string.IsNullOrEmpty(payload))
            text += $" · {payload}";
        return TextSummary(Truncate(text, SummaryMaxLength));
    }

    private static CollapsedSummary SummarizeConfirm(string? argumentsJson)
    {
        var rationale = Truncate(GetString(argumentsJson, "rationale"), 40);
        return TextSummary(string.IsNullOrEmpty(rationale) ? "Confirmed" : $"Confirmed · {rationale}");
    }

    private static string SummarizeWaitForSeconds(string? argumentsJson)
    {
        var seconds = GetInt(argumentsJson, "seconds");
        return seconds is null ? "Wait" : $"Wait {seconds}s";
    }

    private static CollapsedSummary SummarizeJsonDynamicToolchain(string? resultContent, bool hasResult)
    {
        if (!hasResult || string.IsNullOrWhiteSpace(resultContent))
            return TextSummary("toolchain");

        try
        {
            using var doc = JsonDocument.Parse(resultContent);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            var steps = root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array
                ? stepsEl.GetArrayLength()
                : 0;
            var prefix = string.Equals(status, "error", StringComparison.Ordinal) ? "toolchain error" : "toolchain";
            return TextSummary(Truncate($"{prefix}: {steps} steps", SummaryMaxLength));
        }
        catch (JsonException)
        {
            return TextSummary("toolchain");
        }
    }

    private static string SummarizeDropTurnContext(string? argumentsJson)
    {
        var ids = GetStringArray(argumentsJson, "turnIds");
        var reason = Truncate(GetString(argumentsJson, "reason"), 24);
        var countPart = ids.Count == 0
            ? "drop turns"
            : ids.Count == 1
                ? Truncate($"drop {ids[0]}", 40)
                : $"drop {ids.Count} turns";
        if (string.IsNullOrEmpty(reason))
            return Truncate(countPart, SummaryMaxLength);
        return Truncate($"{countPart} · {reason}", SummaryMaxLength);
    }

    private static string SummarizeRestoreTurnContext(string? argumentsJson)
    {
        var ids = GetStringArray(argumentsJson, "turnIds");
        var reason = Truncate(GetString(argumentsJson, "reason"), 24);
        var countPart = ids.Count == 0
            ? "restore turns"
            : ids.Count == 1
                ? Truncate($"restore {ids[0]}", 40)
                : $"restore {ids.Count} turns";
        if (string.IsNullOrEmpty(reason))
            return Truncate(countPart, SummaryMaxLength);
        return Truncate($"{countPart} · {reason}", SummaryMaxLength);
    }

    private static CollapsedSummary SummarizeWebFetch(string? argumentsJson)
    {
        var host = UrlHost(GetString(argumentsJson, "url")) ?? "url";
        var mode = GetBool(argumentsJson, "fullHtml") ? "full" : "summarized";
        return TextSummary(Truncate($"{host} · {mode}", SummaryMaxLength));
    }

    private static string? ExtractSection(string text, string marker)
    {
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var start = idx + marker.Length;
        while (start < text.Length && (text[start] == '\n' || text[start] == '\r'))
            start++;

        var nextStdout = text.IndexOf("--- stdout ---", start, StringComparison.Ordinal);
        var nextStderr = text.IndexOf("--- stderr ---", start, StringComparison.Ordinal);
        var end = text.Length;
        if (marker == "--- stdout ---" && nextStderr >= 0)
            end = nextStderr;
        else if (marker == "--- stderr ---" && nextStdout >= 0 && nextStdout > start)
            end = nextStdout;

        // Prefer the other section boundary correctly:
        if (marker == "--- stdout ---")
        {
            var other = text.IndexOf("--- stderr ---", start, StringComparison.Ordinal);
            if (other >= 0)
                end = other;
        }
        else if (marker == "--- stderr ---")
        {
            var other = text.IndexOf("--- stdout ---", start, StringComparison.Ordinal);
            if (other >= 0)
                end = other;
        }

        return text[start..end].TrimEnd();
    }

    private static string Quote(string text) => string.IsNullOrEmpty(text) ? "\"\"" : $"\"{text}\"";

    private static string CompactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";
        return json.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
    }

    private static bool TryParseObject(string? json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetPropString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        return prop.GetString();
    }
}

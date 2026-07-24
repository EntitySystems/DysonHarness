using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Executes workspace-scoped MCP tools against a work directory root, plus RenameSession,
/// ShellExecute, and in-process web search/fetch tools.
/// Other catalog tools return a not-implemented stub result.
/// </summary>
public sealed class DysonWorkspaceToolExecutor
{
    private readonly DysonAgentSession _session;
    private readonly string _workRoot;
    private readonly DysonSessionStore? _store;

    public DysonWorkspaceToolExecutor(
        DysonAgentSession session,
        string workDirectoryAbsolutePath,
        DysonSessionStore? store = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workRoot = Path.GetFullPath(workDirectoryAbsolutePath);
        _store = store;
    }

    public async Task<DysonToolCallResult> ExecuteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return call.ToolName switch
            {
                "RenameSession" => await RenameSessionAsync(call, cancellationToken).ConfigureAwait(false),
                "ReadFile" => await ReadFileAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateFile" => await CreateFileAsync(call, cancellationToken).ConfigureAwait(false),
                "WriteFile" => await WriteFileAsync(call, cancellationToken).ConfigureAwait(false),
                "Grep" => await GrepAsync(call, cancellationToken).ConfigureAwait(false),
                "ListDirectory" => await ListDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateDirectory" => await CreateDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                "ShellExecute" => await ShellExecuteAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeSearch" => await FreeSearchAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeSearchAdvanced" => await FreeSearchAdvancedAsync(call, cancellationToken).ConfigureAwait(false),
                "SearchWithSynthesis" => await SearchWithSynthesisAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeExtract" => await FreeExtractAsync(call, cancellationToken).ConfigureAwait(false),
                "WebFetch" => await WebFetchAsync(call, cancellationToken).ConfigureAwait(false),
                "FetchGithubReadme" => await FetchGithubReadmeAsync(call, cancellationToken).ConfigureAwait(false),
                _ => Stub(call),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(call, "Tool execution was cancelled.");
        }
        catch (Exception ex)
        {
            return Error(call, $"{call.ToolName} failed: {ex.Message}");
        }
    }

    private static DysonToolCallResult Stub(DysonToolCall call) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = $"{call.ToolName} is not implemented yet.",
        };

    private static DysonToolCallResult Ok(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = content,
        };

    private static DysonToolCallResult Error(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = true,
            Content = content,
        };

    private async Task<DysonToolCallResult> RenameSessionAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string? title = null;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            if (doc.RootElement.TryGetProperty("title", out var titleProp))
                title = titleProp.GetString();
        }
        catch (JsonException)
        {
            return Error(call, "RenameSession: invalid JSON arguments.");
        }

        var rename = await _session.RenameAsync(title ?? "", cancellationToken).ConfigureAwait(false);
        if (rename.IsError)
            return Error(call, rename.Error);

        if (_store is not null && _session.PersistenceId != Guid.Empty)
        {
            var persist = await _store.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    Title = _session.DisplayTitle,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
                return Error(call, persist.Error);

            var renamedLog = DysonSessionLogPayload.CreateEntry(
                _session.PersistenceId,
                DysonSessionLogKind.SessionRenamed,
                new DysonSessionLogSessionRenamed(_session.DisplayTitle!));

            await _store.AppendLogAsync(renamedLog, cancellationToken).ConfigureAwait(false);
        }

        return Ok(call, $"Renamed session to \"{_session.DisplayTitle}\".");
    }

    private async Task<DysonToolCallResult> ReadFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (!File.Exists(resolved.Value))
            return Error(call, $"File not found: {path.Value}");

        var lines = await File.ReadAllLinesAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        var offset = GetInt(doc.RootElement, "offset") ?? 1;
        var limit = GetInt(doc.RootElement, "limit");
        if (offset < 1)
            offset = 1;

        var start = Math.Min(offset - 1, lines.Length);
        var take = limit is null ? lines.Length - start : Math.Max(0, limit.Value);
        var slice = lines.Skip(start).Take(take);

        var sb = new StringBuilder();
        var lineNo = start + 1;
        foreach (var line in slice)
        {
            sb.Append(lineNo);
            sb.Append('|');
            sb.AppendLine(line);
            lineNo++;
        }

        return Ok(call, sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd());
    }

    private async Task<DysonToolCallResult> CreateFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var overwrite = doc.RootElement.TryGetProperty("overwrite", out var o)
            && o.ValueKind == JsonValueKind.True;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (File.Exists(resolved.Value) && !overwrite)
            return Error(call, $"File already exists: {path.Value}");

        var dir = Path.GetDirectoryName(resolved.Value);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(resolved.Value, content, cancellationToken).ConfigureAwait(false);
        return Ok(call, $"Created {path.Value} ({content.Length} chars).");
    }

    private async Task<DysonToolCallResult> WriteFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (!File.Exists(resolved.Value)
            && !(doc.RootElement.TryGetProperty("content", out var fullContentProp)
                 && fullContentProp.ValueKind == JsonValueKind.String))
        {
            return Error(call, $"File not found: {path.Value}");
        }

        if (doc.RootElement.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.String
            && !doc.RootElement.TryGetProperty("old_text", out _)
            && !doc.RootElement.TryGetProperty("edits", out _))
        {
            var dir = Path.GetDirectoryName(resolved.Value);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var full = contentProp.GetString() ?? "";
            await File.WriteAllTextAsync(resolved.Value, full, cancellationToken).ConfigureAwait(false);
            return Ok(call, $"Wrote full content to {path.Value} ({full.Length} chars).");
        }

        var text = await File.ReadAllTextAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        var edits = new List<(string Old, string New)>();

        if (doc.RootElement.TryGetProperty("old_text", out var oldProp)
            && doc.RootElement.TryGetProperty("new_text", out var newProp))
        {
            edits.Add((oldProp.GetString() ?? "", newProp.GetString() ?? ""));
        }

        if (doc.RootElement.TryGetProperty("edits", out var editsArr)
            && editsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var edit in editsArr.EnumerateArray())
            {
                if (!edit.TryGetProperty("old_text", out var o) || !edit.TryGetProperty("new_text", out var n))
                    continue;
                edits.Add((o.GetString() ?? "", n.GetString() ?? ""));
            }
        }

        if (edits.Count == 0)
            return Error(call, "WriteFile: provide content, or old_text/new_text, or edits[].");

        var applied = 0;
        foreach (var (oldText, newText) in edits)
        {
            if (string.IsNullOrEmpty(oldText))
                return Error(call, "WriteFile: old_text must be non-empty.");

            var idx = text.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0)
                return Error(call, $"WriteFile: old_text not found in {path.Value}.");

            text = string.Concat(text.AsSpan(0, idx), newText, text.AsSpan(idx + oldText.Length));
            applied++;
        }

        await File.WriteAllTextAsync(resolved.Value, text, cancellationToken).ConfigureAwait(false);
        return Ok(call, $"Applied {applied} edit(s) to {path.Value}.");
    }

    private async Task<DysonToolCallResult> GrepAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var pattern = RequireString(doc.RootElement, "pattern");
        if (pattern.IsError)
            return Error(call, pattern.Error);

        var searchPath = doc.RootElement.TryGetProperty("path", out var pathProp)
            ? pathProp.GetString() ?? "."
            : ".";
        var glob = doc.RootElement.TryGetProperty("glob", out var globProp)
            ? globProp.GetString()
            : null;
        var caseInsensitive = doc.RootElement.TryGetProperty("caseInsensitive", out var ci)
            && ci.ValueKind == JsonValueKind.True;
        var maxMatches = GetInt(doc.RootElement, "maxMatches") ?? 100;

        var resolved = ResolveUnderWorkRoot(searchPath);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        Regex regex;
        try
        {
            regex = new Regex(
                pattern.Value,
                (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None)
                | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Error(call, $"Invalid regex: {ex.Message}");
        }

        IEnumerable<string> files;
        if (File.Exists(resolved.Value))
        {
            files = [resolved.Value];
        }
        else if (Directory.Exists(resolved.Value))
        {
            var option = SearchOption.AllDirectories;
            files = string.IsNullOrWhiteSpace(glob)
                ? Directory.EnumerateFiles(resolved.Value, "*", option)
                : Directory.EnumerateFiles(resolved.Value, glob, option);
        }
        else
        {
            return Error(call, $"Path not found: {searchPath}");
        }

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnderWorkRoot(file))
                continue;

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var rel = Path.GetRelativePath(_workRoot, file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                    continue;

                sb.Append(rel);
                sb.Append(':');
                sb.Append(i + 1);
                sb.Append(':');
                sb.AppendLine(lines[i]);
                matches++;
                if (matches >= maxMatches)
                    return Ok(call, sb.ToString().TrimEnd() + $"\n… capped at {maxMatches} matches");
            }
        }

        return Ok(call, matches == 0 ? "No matches." : sb.ToString().TrimEnd());
    }

    private Task<DysonToolCallResult> ListDirectoryAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Task.FromResult(Error(call, path.Error));

        var recursive = doc.RootElement.TryGetProperty("recursive", out var r)
            && r.ValueKind == JsonValueKind.True;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Task.FromResult(Error(call, resolved.Error));

        if (!Directory.Exists(resolved.Value))
            return Task.FromResult(Error(call, $"Directory not found: {path.Value}"));

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.EnumerateFileSystemEntries(resolved.Value, "*", option)
            .Take(500)
            .Select(e =>
            {
                var rel = Path.GetRelativePath(_workRoot, e);
                var kind = Directory.Exists(e) ? "dir" : "file";
                return $"{kind}\t{rel}";
            });

        var text = string.Join('\n', entries);
        return Task.FromResult(Ok(call, string.IsNullOrEmpty(text) ? "(empty)" : text));
    }

    private Task<DysonToolCallResult> CreateDirectoryAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Task.FromResult(Error(call, path.Error));

        var createParents = !doc.RootElement.TryGetProperty("createParents", out var cp)
            || cp.ValueKind != JsonValueKind.False;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Task.FromResult(Error(call, resolved.Error));

        if (createParents)
            Directory.CreateDirectory(resolved.Value);
        else
            Directory.CreateDirectory(resolved.Value); // CreateDirectory always creates parents on .NET

        return Task.FromResult(Ok(call, $"Created directory {path.Value}."));
    }

    private async Task<DysonToolCallResult> ShellExecuteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var shellName = RequireString(doc.RootElement, "shell");
        if (shellName.IsError)
            return Error(call, shellName.Error);

        var command = RequireString(doc.RootElement, "command");
        if (command.IsError)
            return Error(call, command.Error);

        if (!Enum.TryParse<DysonShellType>(shellName.Value, ignoreCase: true, out var shellType))
            return Error(call, $"Unknown shell '{shellName.Value}'.");

        var available = _session.Config.AvailableShellTypes;
        if (!available.Contains(shellType))
        {
            var listed = string.Join(", ", available);
            return Error(call, $"Shell '{shellType}' is not available for this session. Available: {listed}.");
        }

        var workDirRel = doc.RootElement.TryGetProperty("workingDirectory", out var wdProp)
            ? wdProp.GetString()
            : null;
        var workDir = string.IsNullOrWhiteSpace(workDirRel)
            ? Result<string, string>.AsValue(_workRoot)
            : ResolveUnderWorkRoot(workDirRel);
        if (workDir.IsError)
            return Error(call, workDir.Error);

        if (!Directory.Exists(workDir.Value))
            return Error(call, $"Working directory not found: {workDirRel ?? "."}");

        var timeoutMs = GetInt(doc.RootElement, "timeoutMs");
        DysonShell shell;
        try
        {
            shell = DysonShell.Create(shellType);
        }
        catch (Exception ex)
        {
            return Error(call, ex.Message);
        }

        var run = await shell.ExecuteAsync(command.Value, workDir.Value, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (run.IsError)
            return Error(call, run.Error);

        var r = run.Value;
        var sb = new StringBuilder();
        sb.Append("exitCode=");
        sb.Append(r.ExitCode);
        if (r.TimedOut)
            sb.Append(" timedOut=true");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(r.Stdout))
        {
            sb.AppendLine("--- stdout ---");
            sb.AppendLine(r.Stdout.TrimEnd());
        }

        if (!string.IsNullOrEmpty(r.Stderr))
        {
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(r.Stderr.TrimEnd());
        }

        var content = sb.ToString().TrimEnd();
        return r.TimedOut || r.ExitCode != 0
            ? Error(call, content)
            : Ok(call, string.IsNullOrEmpty(content) ? "(no output)" : content);
    }

    private async Task<DysonToolCallResult> FreeSearchAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var options = ParseSearchOptions(call, defaultCount: 10, waterfallDefault: false, enrichDefault: false);
        if (options.IsError)
            return Error(call, options.Error);

        var result = await SearchOrchestrator.FreeSearchAsync(options.Value, cancellationToken)
            .ConfigureAwait(false);
        return result.IsError
            ? Error(call, result.Error)
            : Ok(call, SearchOrchestrator.ToJson(result.Value));
    }

    private async Task<DysonToolCallResult> FreeSearchAdvancedAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var options = ParseSearchOptions(call, defaultCount: 5, waterfallDefault: true, enrichDefault: true);
        if (options.IsError)
            return Error(call, options.Error);

        var result = await SearchOrchestrator.FreeSearchAdvancedAsync(options.Value, cancellationToken)
            .ConfigureAwait(false);
        return result.IsError
            ? Error(call, result.Error)
            : Ok(call, SearchOrchestrator.ToJson(result.Value));
    }

    private async Task<DysonToolCallResult> SearchWithSynthesisAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var options = ParseSearchOptions(call, defaultCount: 10, waterfallDefault: true, enrichDefault: true);
        if (options.IsError)
            return Error(call, options.Error);

        var result = await SearchOrchestrator.SearchWithSynthesisAsync(options.Value, cancellationToken)
            .ConfigureAwait(false);
        return result.IsError
            ? Error(call, result.Error)
            : Ok(call, SearchOrchestrator.ToJson(result.Value));
    }

    private async Task<DysonToolCallResult> FreeExtractAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var url = RequireString(doc.RootElement, "url");
        if (url.IsError)
            return Error(call, url.Error);

        var maxLength = GetInt(doc.RootElement, "maxLength") ?? 5000;
        var result = await SearchFetch.FreeExtractAsync(url.Value, maxLength, cancellationToken)
            .ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, result.Value);
    }

    private async Task<DysonToolCallResult> WebFetchAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var url = RequireString(doc.RootElement, "url");
        if (url.IsError)
            return Error(call, url.Error);

        var maxBytes = GetInt(doc.RootElement, "maxBytes");
        var result = await SearchFetch.WebFetchAsync(url.Value, maxBytes, cancellationToken)
            .ConfigureAwait(false);
        return result.IsError
            ? Error(call, result.Error)
            : Ok(call, SearchFetch.WebFetchToJson(result.Value));
    }

    private async Task<DysonToolCallResult> FetchGithubReadmeAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var url = RequireString(doc.RootElement, "url");
        if (url.IsError)
            return Error(call, url.Error);

        var result = await SearchFetch.FetchGithubReadmeAsync(url.Value, cancellationToken)
            .ConfigureAwait(false);
        return result.IsError ? Error(call, result.Error) : Ok(call, result.Value);
    }

    private Result<SearchOptions, string> ParseSearchOptions(
        DysonToolCall call,
        int defaultCount,
        bool waterfallDefault,
        bool enrichDefault)
    {
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var query = RequireString(root, "query");
            if (query.IsError)
                return Result<SearchOptions, string>.AsError(query.Error);

            List<string>? engines = null;
            if (root.TryGetProperty("engines", out var enginesProp) && enginesProp.ValueKind == JsonValueKind.Array)
            {
                engines = [];
                foreach (var item in enginesProp.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        engines.Add(s);
                }
            }

            List<string>? includeDomains = ReadStringArray(root, "includeDomains");
            List<string>? excludeDomains = ReadStringArray(root, "excludeDomains");

            var waterfall = waterfallDefault;
            if (root.TryGetProperty("waterfall", out var wf))
                waterfall = wf.ValueKind != JsonValueKind.False;

            var enrich = enrichDefault;
            if (root.TryGetProperty("enrich", out var en))
                enrich = en.ValueKind != JsonValueKind.False;

            double waterfallMinConfidence = 0.6;
            if (root.TryGetProperty("waterfallMinConfidence", out var wmc)
                && wmc.ValueKind == JsonValueKind.Number
                && wmc.TryGetDouble(out var wmcVal))
            {
                waterfallMinConfidence = wmcVal;
            }

            return Result<SearchOptions, string>.AsValue(new SearchOptions
            {
                Query = query.Value,
                Count = GetInt(root, "count") ?? defaultCount,
                Engines = engines,
                MinConfidence = GetInt(root, "minConfidence") ?? 1,
                IncludeDomains = includeDomains,
                ExcludeDomains = excludeDomains,
                Waterfall = waterfall,
                WaterfallMinResults = GetInt(root, "waterfallMinResults") ?? 3,
                WaterfallMinConfidence = waterfallMinConfidence,
                Enrich = enrich,
                EnrichMax = GetInt(root, "enrichMax") ?? 3,
                BraveApiKey = SearchOrchestrator.ResolveBraveApiKey(_session.Config),
            });
        }
        catch (JsonException)
        {
            return Result<SearchOptions, string>.AsError($"{call.ToolName}: invalid JSON arguments.");
        }
    }

    private static List<string>? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }

        return list.Count > 0 ? list : null;
    }

    private Result<string, string> ResolveUnderWorkRoot(string path)
    {
        try
        {
            var combined = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_workRoot, path));

            if (!IsUnderWorkRoot(combined))
                return Result<string, string>.AsError($"Path escapes work directory: {path}");

            return Result<string, string>.AsValue(combined);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }
    }

    private bool IsUnderWorkRoot(string fullPath)
    {
        var root = _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        return full.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ArgsOrEmpty(DysonToolCall call) =>
        string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;

    private static Result<string, string> RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return Result<string, string>.AsError($"Missing required string field '{name}'.");

        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return Result<string, string>.AsError($"Field '{name}' must be non-empty.");

        return Result<string, string>.AsValue(value);
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
            return i;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
